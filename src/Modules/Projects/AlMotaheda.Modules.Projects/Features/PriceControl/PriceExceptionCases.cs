using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Prices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Features.PriceControl;

/// <summary>
/// Opens and withdraws price exception cases, in the caller's save, with the alerts to the people who decide and
/// reconcile them (the project's managers, the Executives, the accountants; never the submitter). The Platform
/// outbox turns the alert rows into stored notifications after the commit, so nobody hears of a case that rolled back.
/// </summary>
internal sealed class PriceExceptionCases(ProjectsDbContext db, CostAlerts alerts, IOptions<ProjectsOptions> options, CompanyClock clock, ProjectsLog log)
{
    public PriceCheckPolicy Policy => options.Value.PriceCheckPolicy;

    /// <summary>For a joined context on another module's transaction (<see cref="Integration.PriceControlContract"/>).</summary>
    public CompanyClock Clock => clock;

    public Access.Contracts.IUserDirectory Users => alerts.Users;

    /// <summary>Whether cases gate transactions (approval, payment) under the company's policy.</summary>
    public bool Gates => PriceGate.Gates(Policy);

    /// <summary>
    /// Adds a case for a transaction above its range (saved by the caller, in its transaction). The transaction must
    /// already have its id. Returns the case; its id is known after the save.
    /// </summary>
    public async Task<PriceException> OpenAsync(
        string sourceType, int sourceId, int projectId, int? supplierId, PriceReference price, string summary, DateOnly transactionDate,
        int submittedBy, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var variance = price.Comparison;
        var exception = new PriceException
        {
            SourceType = sourceType,
            SourceId = sourceId,
            ProjectId = projectId,
            SupplierId = supplierId,
            PriceItemId = price.PriceItemId,
            PriceRangeId = price.Range?.RangeId,
            Unit = price.Unit,
            Quantity = price.ComparedQuantity,
            UnitPrice = price.ComparedPrice,
            ReferenceMinPrice = price.Range?.MinPrice,
            ReferenceMaxPrice = price.Range?.MaxPrice,
            VariancePerUnit = variance.DifferencePerUnit,
            VarianceTotal = variance.TotalDifference,
            VariancePercent = variance.PercentAbove(price.Range?.MaxPrice),
            Reason = null,
            Summary = summary.Length > Data.Configurations.PriceExceptionConfiguration.SummaryMaxLength ? summary[..Data.Configurations.PriceExceptionConfiguration.SummaryMaxLength] : summary,
            TransactionDate = transactionDate,
            SubmittedBy = submittedBy,
            SubmittedAt = now,
        };
        db.PriceExceptions.Add(exception);

        // One alert per person per record: a resubmitted record (its earlier case withdrawn) tells nobody twice.
        var alreadyTold = await db.PriceExceptionAlerts.AsNoTracking()
            .Where(alert => alert.Exception.SourceType == sourceType && alert.Exception.SourceId == sourceId)
            .Select(alert => alert.RecipientUserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var recipient in (await alerts.CustodyWatchersAsync(projectId, submittedBy, cancellationToken)).Except(alreadyTold))
        {
            db.PriceExceptionAlerts.Add(new PriceExceptionAlert { Exception = exception, ProjectId = projectId, RecipientUserId = recipient, CreatedAt = now });
        }

        return exception;
    }

    /// <summary>The same, from another module's draft (<see cref="IPriceControl.OpenExceptionAsync"/>).</summary>
    public async Task<PriceException> OpenAsync(PriceExceptionDraft draft, CancellationToken cancellationToken)
    {
        var range = draft.Verdict.RangeId is { } rangeId
            ? new ApplicableRange(rangeId, draft.SupplierId, draft.Verdict.MinPrice ?? 0, draft.Verdict.MaxPrice ?? 0, PriceCurrencies.Egp, draft.TransactionDate, null, draft.Verdict.ReferencePrice)
            : null;
        var comparison = PriceCheck.Compare(draft.UnitPrice, draft.Quantity, range?.MinPrice, range?.MaxPrice);
        var price = new PriceReference(draft.PriceItemId, true, range, comparison, draft.Unit, draft.UnitPrice, draft.Quantity);
        var exception = await OpenAsync(draft.SourceType, draft.SourceId, draft.ProjectId, draft.SupplierId, price, draft.Summary, draft.TransactionDate, draft.SubmittedBy, cancellationToken);
        exception.Reason = draft.Reason;
        return exception;
    }

    /// <summary>
    /// The transaction was changed by its submitter: every case still counting against it (pending or rejected) is
    /// withdrawn, so a corrected price starts clean and a still-high price opens a fresh case for the Executive.
    /// </summary>
    public async Task<int> WithdrawOpenAsync(string sourceType, int sourceId, int userId, CancellationToken cancellationToken)
    {
        var open = await db.PriceExceptions
            .Where(exception => exception.SourceType == sourceType && exception.SourceId == sourceId
                                && (exception.Status == PriceExceptionStatus.Pending || exception.Status == PriceExceptionStatus.Rejected))
            .ToListAsync(cancellationToken);
        foreach (var exception in open)
        {
            exception.Withdraw(userId, clock.UtcNow);
            log.PriceExceptionWithdrawn(userId, exception.Id);
        }

        return open.Count;
    }

    /// <summary>The newest case of a transaction, or <c>null</c>.</summary>
    public Task<PriceException?> NewestAsync(string sourceType, int sourceId, CancellationToken cancellationToken) =>
        db.PriceExceptions.AsNoTracking()
            .Where(exception => exception.SourceType == sourceType && exception.SourceId == sourceId)
            .OrderByDescending(exception => exception.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The newest case of each of several records of one kind, by record id.</summary>
    public static async Task<IReadOnlyDictionary<int, PriceException>> NewestAsync(
        ProjectsDbContext db, string sourceType, IReadOnlyCollection<int> sourceIds, CancellationToken cancellationToken)
    {
        if (sourceIds.Count == 0)
        {
            return new Dictionary<int, PriceException>();
        }

        var rows = await db.PriceExceptions.AsNoTracking()
            .Where(exception => exception.SourceType == sourceType && sourceIds.Contains(exception.SourceId))
            .OrderByDescending(exception => exception.Id)
            .ToListAsync(cancellationToken);
        return rows.GroupBy(exception => exception.SourceId).ToDictionary(group => group.Key, group => group.First());
    }

    /// <summary>The refusal to answer before paying anything on these records, or <c>null</c>. Money waits for the Executive.</summary>
    public async Task<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult?> PaymentRefusalAsync(IReadOnlyCollection<int> expenseIds, int actorId, CancellationToken cancellationToken)
    {
        if (!Gates)
        {
            return null;
        }

        var newest = await NewestAsync(db, PriceExceptionSources.ProjectExpense, expenseIds, cancellationToken);
        foreach (var (expenseId, exception) in newest)
        {
            if (PriceGate.PaymentBlocked(exception.Status, Policy))
            {
                log.PriceGateRefused(actorId, PriceExceptionSources.ProjectExpense, expenseId, exception.Id, exception.Status.ToString());
                return exception.Status == PriceExceptionStatus.Rejected
                    ? PriceControlProblems.ExceptionWasRejected(exception.Id)
                    : PriceControlProblems.ExceptionIsPending(exception.Id);
            }
        }

        return null;
    }

    /// <summary>The same check for a source module's settlement: the message of the refusal, or <c>null</c>.</summary>
    public async Task<string?> PaymentBlockerAsync(IReadOnlyCollection<int> expenseIds, CancellationToken cancellationToken)
    {
        if (!Gates)
        {
            return null;
        }

        var newest = await NewestAsync(db, PriceExceptionSources.ProjectExpense, expenseIds, cancellationToken);
        var blocked = newest.Values.FirstOrDefault(exception => PriceGate.PaymentBlocked(exception.Status, Policy));
        return blocked is null ? null : $"Price exception #{blocked.Id} is {blocked.Status}: the Executive's decision is needed before this record is paid.";
    }
}
