using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Data.Configurations;
using AlMotaheda.Modules.Office.Features.Custody;
using AlMotaheda.Modules.Office.Features.Expenses;
using AlMotaheda.Modules.Office.Features.Ledger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Features.Reports;

/// <summary>
/// The office screens' summaries: <c>GET /api/office/home</c> (the custodian's mobile home and the reviewers'
/// counters), <c>GET /api/office/review-queue</c> (what waits for a reviewer), <c>GET /api/office/reports/summary</c>
/// (spending by category, month and vendor, with the custody movements of the period) and
/// <c>GET /api/office/alerts/{id}</c> (where a notification leads).
/// </summary>
internal static class OfficeOverviewEndpoints
{
    internal sealed record Policy(decimal RoutineApprovalLimit, IReadOnlyList<string> Categories, int ImageMaxMegabytes, int MaxExpenseAgeDays);

    internal sealed record HomeCounts(int Returned, int InReview, int WaitingForPrice, int WaitingForFunds, int FundsToConfirm, int OpenSpendRequests, int ToReview);

    internal sealed record HomeResponse(
        bool IsCustodian, bool IsReviewer, IReadOnlyList<OfficeCustodyView> Custodies, HomeCounts Counts, IReadOnlyList<OfficeExpenseRow> Recent, Policy Policy);

    internal sealed record QueueItem(string RecordType, int RecordId, int CustodyId, string CustodyNameAr, string CustodyNameEn, string Summary, decimal? Amount,
        string Status, IReadOnlyList<string> Reasons, DateTime Since, OfficePersonRef? From);

    internal sealed record QueueResponse(IReadOnlyList<QueueItem> Items, int Total);

    internal sealed record Bucket(string Key, int Count, decimal Amount);

    internal sealed record SummaryResponse(
        DateOnly? From,
        DateOnly? To,
        int? CustodyId,
        decimal OpeningBalance,
        decimal FundingReceived,
        decimal ExpensesPosted,
        decimal Reversals,
        decimal Returns,
        decimal Adjustments,
        decimal ClosingBalance,
        decimal PendingExpenses,
        int ExpenseCount,
        IReadOnlyList<Bucket> ByCategory,
        IReadOnlyList<Bucket> ByMonth,
        IReadOnlyList<Bucket> ByVendor,
        IReadOnlyList<Bucket> ByReviewReason);

    internal sealed record AlertTarget(int AlertId, string Kind, int CustodyId, string SourceType, int SourceId, DateTime CreatedAt);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/office/home", HomeAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeHome")
            .WithSummary("The custodian's office home: balances, waiting work and recent expenses");

        api.MapGet("/office/review-queue", QueueAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("GetOfficeReviewQueue")
            .WithSummary("Office records waiting for the caller's review (office Approve)");

        api.MapGet("/office/reports/summary", SummaryAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeSummary")
            .WithSummary("Office spending and custody movements of a period (custodyId, from, to)");

        api.MapGet("/office/alerts/{alertId:int}", AlertAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeAlert")
            .WithSummary("Where an office notification leads (its recipient only)");
    }

    internal static async Task<Results<Ok<HomeResponse>, ProblemHttpResult>> HomeAsync(
        HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IOptions<OfficeOptions> settings, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var options = settings.Value;
        var held = await db.Custodies.AsNoTracking()
            .Where(custody => custody.HolderUserId == caller.UserId && custody.Status != OfficeCustodyStatus.Closed)
            .OrderBy(custody => custody.NameEn)
            .ToListAsync(cancellationToken);
        var shown = held.Count > 0 || !caller.SeesAll
            ? held
            : await db.Custodies.AsNoTracking().Where(custody => custody.Status != OfficeCustodyStatus.Closed).OrderBy(custody => custody.NameEn).ToListAsync(cancellationToken);
        var ids = shown.Select(custody => custody.Id).ToList();

        var expenses = db.Expenses.AsNoTracking().Where(expense => ids.Contains(expense.CustodyId));
        var counts = new HomeCounts(
            await expenses.CountAsync(expense => expense.Status == OfficeExpenseStatus.Returned && expense.CreatedBy == caller.UserId, cancellationToken),
            await expenses.CountAsync(expense => expense.Status == OfficeExpenseStatus.Submitted || expense.Status == OfficeExpenseStatus.PartlyApproved, cancellationToken),
            await expenses.CountAsync(expense => expense.PriceExceptionId != null && expense.PostingStatus == OfficePostingStatus.NotPosted
                                                 && expense.Status != OfficeExpenseStatus.Rejected, cancellationToken),
            await expenses.CountAsync(expense => expense.Status == OfficeExpenseStatus.Approved && expense.PostingStatus == OfficePostingStatus.NotPosted, cancellationToken),
            await db.FundingRequests.CountAsync(funding => ids.Contains(funding.CustodyId) && funding.Status == OfficeFundingStatus.Disbursed, cancellationToken),
            await db.SpendRequests.CountAsync(spend => ids.Contains(spend.CustodyId)
                                                       && (spend.Status == OfficeSpendStatus.Submitted || spend.Status == OfficeSpendStatus.Approved), cancellationToken),
            caller.IsReviewer ? (await QueueItemsAsync(db, users, caller, cancellationToken)).Count : 0);

        var recent = await ExpenseQueries.RowsAsync(db, users,
            expenses.OrderByDescending(expense => expense.SubmittedAt).ThenByDescending(expense => expense.Id).Take(10), cancellationToken);

        return TypedResults.Ok(new HomeResponse(
            held.Count > 0, caller.IsReviewer, await OfficeCustodyEndpoints.ViewsAsync(db, users, shown, options, cancellationToken), counts, recent,
            new Policy(options.RoutineApprovalLimit, OfficeExpenseCategories.All, options.ImageMaxMegabytes, options.MaxExpenseAgeDays)));
    }

    internal static async Task<Results<Ok<QueueResponse>, ProblemHttpResult>> QueueAsync(
        HttpContext httpContext, OfficeDbContext db, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var items = await QueueItemsAsync(db, users, caller, cancellationToken);
        return TypedResults.Ok(new QueueResponse(items, items.Count));
    }

    /// <summary>
    /// What the reviewer can act on now: expenses in review they haven't approved this round, funding and repair
    /// requests waiting, counts with a difference. Never the reviewer's own records or the custodies they hold.
    /// </summary>
    private static async Task<IReadOnlyList<QueueItem>> QueueItemsAsync(OfficeDbContext db, IUserDirectory users, OfficeCaller caller, CancellationToken cancellationToken)
    {
        var custodies = await db.Custodies.AsNoTracking().Where(custody => custody.HolderUserId != caller.UserId).ToDictionaryAsync(custody => custody.Id, cancellationToken);
        var ids = custodies.Keys.ToList();

        var expenses = await db.Expenses.AsNoTracking()
            .Where(expense => ids.Contains(expense.CustodyId) && expense.CreatedBy != caller.UserId
                              && (expense.Status == OfficeExpenseStatus.Submitted || expense.Status == OfficeExpenseStatus.PartlyApproved)
                              && !db.ExpenseApprovals.Any(approval => approval.ExpenseId == expense.Id && approval.Round == expense.Round && approval.ApproverUserId == caller.UserId))
            .Select(expense => new { expense.Id, expense.CustodyId, expense.Description, expense.Vendor, expense.Amount, expense.Status, expense.ReviewReasons, expense.SubmittedAt, expense.CreatedBy })
            .ToListAsync(cancellationToken);
        var funding = await db.FundingRequests.AsNoTracking()
            .Where(request => ids.Contains(request.CustodyId) && request.RequestedBy != caller.UserId && request.Status == OfficeFundingStatus.Submitted)
            .ToListAsync(cancellationToken);
        var spends = await db.SpendRequests.AsNoTracking()
            .Where(spend => ids.Contains(spend.CustodyId) && spend.RequestedBy != caller.UserId && spend.Status == OfficeSpendStatus.Submitted)
            .ToListAsync(cancellationToken);
        var counts = await db.CashCounts.AsNoTracking()
            .Where(count => ids.Contains(count.CustodyId) && count.CountedBy != caller.UserId && count.Status == OfficeCashCountStatus.Submitted)
            .ToListAsync(cancellationToken);

        var people = await users.FindAsync(
            expenses.Select(row => row.CreatedBy).Concat(funding.Select(row => row.RequestedBy)).Concat(spends.Select(row => row.RequestedBy))
                .Concat(counts.Select(row => row.CountedBy)).Distinct().ToList(),
            cancellationToken);

        QueueItem Item(string type, int id, int custodyId, string summary, decimal? amount, string status, IReadOnlyList<string> reasons, DateTime since, int from) =>
            new(type, id, custodyId, custodies[custodyId].NameAr, custodies[custodyId].NameEn, summary, amount, status, reasons, since, OfficePersonRef.From(from, people));

        return expenses.Select(row => Item(OfficeAttachmentOwners.Expense, row.Id, row.CustodyId, row.Vendor is null ? row.Description : $"{row.Description} · {row.Vendor}",
                row.Amount, row.Status.ToString(), ExpensePolicy.Split(row.ReviewReasons), row.SubmittedAt, row.CreatedBy))
            .Concat(funding.Select(row => Item(OfficeAttachmentOwners.FundingRequest, row.Id, row.CustodyId, row.Reason, row.RequestedAmount, row.Status.ToString(), [],
                row.RequestedAt, row.RequestedBy)))
            .Concat(spends.Select(row => Item(OfficeAttachmentOwners.SpendRequest, row.Id, row.CustodyId, row.Title, row.EstimatedAmount, row.Status.ToString(),
                row.IsEmergency ? [OfficeReviewReasons.Emergency] : [], row.RequestedAt, row.RequestedBy)))
            .Concat(counts.Select(row => Item(OfficeAttachmentOwners.CashCount, row.Id, row.CustodyId, row.Explanation ?? "", row.Difference, row.Status.ToString(), [],
                row.CountedAt, row.CountedBy)))
            .OrderBy(item => item.Since)
            .ToList();
    }

    internal static async Task<Results<Ok<SummaryResponse>, ValidationProblem, ProblemHttpResult>> SummaryAsync(
        HttpContext httpContext, OfficeDbContext db, CancellationToken cancellationToken, int? custodyId = null, string? from = null, string? to = null)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        if (fromDate is { } a && toDate is { } b && a > b)
        {
            errors.Add("to", "to must be on or after from.");
        }

        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var visible = OfficeCustodyEndpoints.Visible(db, caller).Select(custody => custody.Id);
        if (custodyId is { } only)
        {
            if (!await visible.AnyAsync(id => id == only, cancellationToken))
            {
                return OfficeProblems.CustodyNotFound();
            }

            visible = visible.Where(id => id == only);
        }

        var ids = await visible.ToListAsync(cancellationToken);
        var entries = db.LedgerEntries.AsNoTracking().Where(entry => ids.Contains(entry.CustodyId));
        var opening = fromDate is { } first ? await entries.Where(entry => entry.EntryDate < first).SumAsync(entry => (decimal?)entry.Amount, cancellationToken) ?? 0m : 0m;
        var period = await entries
            .Where(entry => (fromDate == null || entry.EntryDate >= fromDate) && (toDate == null || entry.EntryDate <= toDate))
            .GroupBy(entry => entry.Kind)
            .Select(group => new { group.Key, Sum = group.Sum(entry => entry.Amount) })
            .ToDictionaryAsync(row => row.Key, row => row.Sum, cancellationToken);

        // Spending is what was posted (and not reversed): the money that actually left the custodies.
        var spent = await db.Expenses.AsNoTracking()
            .Where(expense => ids.Contains(expense.CustodyId) && expense.PostingStatus == OfficePostingStatus.Posted
                              && (fromDate == null || expense.ExpenseDate >= fromDate) && (toDate == null || expense.ExpenseDate <= toDate))
            .Select(expense => new { expense.Category, expense.ExpenseDate, expense.Vendor, expense.Amount, expense.ReviewReasons })
            .ToListAsync(cancellationToken);
        var pending = await db.Expenses.AsNoTracking()
            .Where(expense => ids.Contains(expense.CustodyId) && expense.PostingStatus == OfficePostingStatus.NotPosted
                              && expense.Status != OfficeExpenseStatus.Rejected && expense.Status != OfficeExpenseStatus.Returned)
            .SumAsync(expense => (decimal?)expense.Amount, cancellationToken) ?? 0m;

        static IReadOnlyList<Bucket> Buckets<T>(IEnumerable<T> rows, Func<T, string> key, Func<T, decimal> amount, int? top = null)
        {
            var buckets = rows.GroupBy(key).Select(group => new Bucket(group.Key, group.Count(), group.Sum(amount))).OrderByDescending(bucket => bucket.Amount);
            return (top is { } limit ? buckets.Take(limit) : buckets).ToList();
        }

        var total = period.Values.Sum();
        return TypedResults.Ok(new SummaryResponse(
            fromDate, toDate, custodyId, opening,
            period.GetValueOrDefault(OfficeLedgerKind.Funding),
            -period.GetValueOrDefault(OfficeLedgerKind.Expense),
            period.GetValueOrDefault(OfficeLedgerKind.Reversal),
            -period.GetValueOrDefault(OfficeLedgerKind.Return),
            period.GetValueOrDefault(OfficeLedgerKind.Adjustment),
            opening + total,
            pending,
            spent.Count,
            Buckets(spent, row => row.Category, row => row.Amount),
            Buckets(spent, row => row.ExpenseDate.ToString("yyyy-MM"), row => row.Amount).OrderBy(bucket => bucket.Key).ToList(),
            Buckets(spent, row => row.Vendor ?? "-", row => row.Amount, 10),
            Buckets(spent.SelectMany(row => ExpensePolicy.Split(row.ReviewReasons).Select(reason => (Reason: reason, row.Amount))), row => row.Reason, row => row.Amount)));
    }

    internal static async Task<Results<Ok<AlertTarget>, ProblemHttpResult>> AlertAsync(
        int alertId, HttpContext httpContext, OfficeDbContext db, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var alert = await db.Alerts.AsNoTracking()
            .Where(candidate => candidate.Id == alertId && candidate.RecipientUserId == caller.UserId)
            .Select(candidate => new { candidate.Id, Kind = EF.Property<string>(candidate, OfficeAlertConfiguration.KindColumn), candidate.CustodyId, candidate.SourceType, candidate.SourceId, candidate.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken);
        return alert is null
            ? OfficeProblems.AlertNotFound()
            : TypedResults.Ok(new AlertTarget(alert.Id, alert.Kind, alert.CustodyId, alert.SourceType, alert.SourceId, alert.CreatedAt));
    }
}
