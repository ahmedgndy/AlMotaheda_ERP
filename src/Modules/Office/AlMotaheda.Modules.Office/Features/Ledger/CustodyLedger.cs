using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Features.Alerts;
using AlMotaheda.Modules.Office.Features.Periods;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Features.Ledger;

/// <summary>
/// A custody's money over all its months, as finance sees it. Only <see cref="Recorded"/> is real cash: the sum of
/// posted ledger entries. The other amounts are what is on its way in or out, kept apart so nothing is counted before
/// it happened. The custodian's own screen shows the current month instead (<see cref="OfficePeriodFigures"/>).
/// </summary>
/// <param name="Recorded">The sum of every posted ledger entry: what the custodian should hold (unsettled months included).</param>
/// <param name="PendingExpenses">Expenses recorded but not posted yet (in review, or approved and waiting).</param>
/// <param name="FundingRequested">Funding asked for and not reviewed yet.</param>
/// <param name="FundingApproved">Funding agreed, waiting for Finance to pay it out.</param>
/// <param name="FundingDisbursed">Paid out by Finance, waiting for the custodian to confirm receiving it.</param>
/// <param name="Commitments">Approved repair and purchase requests not done yet (their estimates).</param>
/// <param name="Available">What may still be spent this month: the month's remaining, less its pending expenses (when the policy says so) and commitments.</param>
internal sealed record OfficeCustodyBalance(
    decimal Recorded,
    decimal PendingExpenses,
    decimal FundingRequested,
    decimal FundingApproved,
    decimal FundingDisbursed,
    decimal Commitments,
    decimal Available,
    decimal LowBalanceThreshold,
    bool IsLow,
    decimal CurrentMonthRemaining);

/// <summary>
/// The only code that writes the ledger. Every posting runs inside the caller's transaction, under the custody's
/// application lock (<see cref="AppLock.Custody"/>), so balances read and written by two requests never interleave.
/// </summary>
internal static class CustodyLedger
{
    public static Task<bool> LockAsync(OfficeDbContext db, int custodyId, CancellationToken cancellationToken) =>
        AppLock.TryAcquireAsync(db, AppLock.Custody(custodyId), cancellationToken);

    public static async Task<decimal> RecordedAsync(OfficeDbContext db, int custodyId, CancellationToken cancellationToken) =>
        await db.LedgerEntries.Where(entry => entry.CustodyId == custodyId).SumAsync(entry => (decimal?)entry.Amount, cancellationToken) ?? 0m;

    public static async Task<OfficeCustodyBalance> BalanceAsync(OfficeDbContext db, OfficeCustody custody, bool reservePending, CancellationToken cancellationToken) =>
        (await BalancesAsync(db, [custody], reservePending, cancellationToken))[custody.Id];

    /// <summary>The balances of several custodies with a fixed number of queries.</summary>
    public static async Task<IReadOnlyDictionary<int, OfficeCustodyBalance>> BalancesAsync(
        OfficeDbContext db, IReadOnlyCollection<OfficeCustody> custodies, bool reservePending, CancellationToken cancellationToken)
    {
        var ids = custodies.Select(custody => custody.Id).ToList();

        var recorded = await db.LedgerEntries.AsNoTracking()
            .Where(entry => ids.Contains(entry.CustodyId))
            .GroupBy(entry => entry.CustodyId)
            .Select(group => new { group.Key, Sum = group.Sum(entry => entry.Amount) })
            .ToDictionaryAsync(row => row.Key, row => row.Sum, cancellationToken);

        var pending = await db.Expenses.AsNoTracking()
            .Where(expense => ids.Contains(expense.CustodyId) && expense.PostingStatus == OfficePostingStatus.NotPosted
                              && (expense.Status == OfficeExpenseStatus.Submitted || expense.Status == OfficeExpenseStatus.PartlyApproved
                                  || expense.Status == OfficeExpenseStatus.Approved))
            .GroupBy(expense => expense.CustodyId)
            .Select(group => new { group.Key, Sum = group.Sum(expense => expense.Amount) })
            .ToDictionaryAsync(row => row.Key, row => row.Sum, cancellationToken);

        var funding = await db.FundingRequests.AsNoTracking()
            .Where(request => ids.Contains(request.CustodyId)
                              && (request.Status == OfficeFundingStatus.Submitted || request.Status == OfficeFundingStatus.Approved
                                  || request.Status == OfficeFundingStatus.Disbursed))
            .Select(request => new { request.CustodyId, request.Status, request.RequestedAmount, request.ApprovedAmount, request.DisbursedAmount })
            .ToListAsync(cancellationToken);

        var commitments = await db.SpendRequests.AsNoTracking()
            .Where(spend => ids.Contains(spend.CustodyId) && spend.Status == OfficeSpendStatus.Approved && spend.EstimatedAmount != null)
            .GroupBy(spend => spend.CustodyId)
            .Select(group => new { group.Key, Sum = group.Sum(spend => spend.EstimatedAmount!.Value) })
            .ToDictionaryAsync(row => row.Key, row => row.Sum, cancellationToken);

        var open = await CustodyPeriods.OpenOfAsync(db, ids, cancellationToken);
        var months = await CustodyPeriods.FiguresAsync(db, open.Values.Select(period => period.Id).ToList(), cancellationToken);

        return custodies.ToDictionary(custody => custody.Id, custody =>
        {
            var own = funding.Where(request => request.CustodyId == custody.Id).ToList();
            var balance = recorded.GetValueOrDefault(custody.Id);
            var waiting = pending.GetValueOrDefault(custody.Id);
            var committed = commitments.GetValueOrDefault(custody.Id);
            var month = open.TryGetValue(custody.Id, out var current) ? months[current.Id] : OfficePeriodFigures.Zero;
            return new OfficeCustodyBalance(
                balance,
                waiting,
                own.Where(request => request.Status == OfficeFundingStatus.Submitted).Sum(request => request.RequestedAmount),
                own.Where(request => request.Status == OfficeFundingStatus.Approved).Sum(request => request.ApprovedAmount ?? 0m),
                own.Where(request => request.Status == OfficeFundingStatus.Disbursed).Sum(request => request.DisbursedAmount ?? 0m),
                committed,
                month.Remaining - (reservePending ? month.PendingAmount : 0m) - committed,
                custody.LowBalanceThreshold,
                IsLow(custody, month),
                month.Remaining);
        });
    }

    /// <summary>Below the threshold in a month that received money (a new month waiting for its allocation is not "low").</summary>
    public static bool IsLow(OfficeCustody custody, OfficePeriodFigures month) =>
        custody.LowBalanceThreshold > 0 && month.Allocated + month.FundingReceived + month.CarriedIn > 0 && month.Remaining < custody.LowBalanceThreshold;

    /// <summary>
    /// What may still be spent in a month: its remaining, less its pending expenses (when the policy reserves them)
    /// and, in the current month, the approved repair and purchase estimates.
    /// </summary>
    public static async Task<decimal> AvailableAsync(
        OfficeDbContext db, OfficeCustodyPeriod period, bool reservePending, CancellationToken cancellationToken)
    {
        var figures = (await CustodyPeriods.FiguresAsync(db, [period.Id], cancellationToken))[period.Id];
        var committed = period.Status == OfficePeriodStatus.Open
            ? await db.SpendRequests.Where(spend => spend.CustodyId == period.CustodyId && spend.Status == OfficeSpendStatus.Approved && spend.EstimatedAmount != null)
                .SumAsync(spend => (decimal?)spend.EstimatedAmount, cancellationToken) ?? 0m
            : 0m;
        return figures.Remaining - (reservePending ? figures.PendingAmount : 0m) - committed;
    }

    /// <summary>Adds one movement (saved with the caller's next save). The unique source index refuses a second posting.</summary>
    public static OfficeLedgerEntry Add(
        OfficeDbContext db,
        int custodyId,
        int periodId,
        OfficeLedgerKind kind,
        decimal amount,
        DateOnly entryDate,
        string sourceType,
        int sourceId,
        int postedBy,
        DateTime now,
        string? reason = null,
        string? reference = null,
        long? reversesEntryId = null)
    {
        var entry = new OfficeLedgerEntry
        {
            CustodyId = custodyId,
            PeriodId = periodId,
            Kind = kind,
            Amount = amount,
            EntryDate = entryDate,
            SourceType = sourceType,
            SourceId = sourceId,
            ReversesEntryId = reversesEntryId,
            Reason = reason,
            Reference = reference,
            PostedAt = now,
            PostedBy = postedBy,
        };
        db.LedgerEntries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Posts the approved expenses of a custody that are ready (every approval in, price case approved when there is
    /// one), oldest first, each in its own month while that month's remaining balance covers it. An expense the
    /// month's cash doesn't cover waits for the next funding. Must run under the custody lock; saves its changes.
    /// Returns the posted expenses.
    /// </summary>
    /// <param name="priceCleared">Whether an expense's price case (if any) is approved; asked only for expenses with a case.</param>
    public static async Task<IReadOnlyList<OfficeExpense>> PostReadyAsync(
        OfficeDbContext db,
        OfficeCustody custody,
        IReadOnlyCollection<OfficeExpense> candidates,
        Func<OfficeExpense, bool> priceCleared,
        int actorUserId,
        DateTime now,
        OfficeAlerts alerts,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        var ready = candidates
            .Where(expense => expense.Status == OfficeExpenseStatus.Approved && expense.PostingStatus == OfficePostingStatus.NotPosted)
            .OrderBy(expense => expense.SubmittedAt).ThenBy(expense => expense.Id)
            .ToList();
        var periodIds = ready.Select(expense => expense.PeriodId).Distinct().ToList();
        var closed = await db.Periods.Where(period => periodIds.Contains(period.Id) && period.Status == OfficePeriodStatus.Closed)
            .Select(period => period.Id).ToListAsync(cancellationToken);
        var balances = new Dictionary<int, decimal>();
        var posted = new List<OfficeExpense>();
        foreach (var expense in ready)
        {
            if (expense.PriceExceptionId is not null && !priceCleared(expense))
            {
                continue;
            }

            // A closed month never changes (it can't close while one of its expenses waits, so this is a safety net).
            if (closed.Contains(expense.PeriodId))
            {
                continue;
            }

            if (!balances.TryGetValue(expense.PeriodId, out var balance))
            {
                balance = await CustodyPeriods.RemainingAsync(db, expense.PeriodId, cancellationToken);
            }

            if (expense.Amount > balance)
            {
                balances[expense.PeriodId] = balance;
                continue;
            }

            var entry = Add(db, custody.Id, expense.PeriodId, OfficeLedgerKind.Expense, -expense.Amount, expense.ExpenseDate, OfficeLedgerSources.Expense,
                expense.Id, actorUserId, now, reference: expense.InvoiceNo);
            await db.SaveChangesAsync(cancellationToken);

            expense.PostingStatus = OfficePostingStatus.Posted;
            expense.LedgerEntryId = entry.Id;
            expense.PostedAt = now;
            balances[expense.PeriodId] = balance - expense.Amount;
            posted.Add(expense);
            log.EntryPosted(actorUserId, custody.Id, entry.Id, entry.Kind.ToString());

            if (expense.CreatedBy != actorUserId)
            {
                alerts.Add<OfficeExpensePostedAlert>(db, [expense.CreatedBy], custody.Id, OfficeAttachmentOwners.Expense, expense.Id, now);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (posted.Count > 0)
        {
            await CheckLowBalanceAsync(db, custody, alerts, log, now, cancellationToken);
        }

        return posted;
    }

    /// <summary>
    /// Warns the custodian and the reviewers once when the current month's remaining balance falls below the
    /// threshold, and re-arms the warning when it is back above (a new month re-arms it once funded). Must run under
    /// the custody lock; saves its changes.
    /// </summary>
    public static async Task CheckLowBalanceAsync(
        OfficeDbContext db, OfficeCustody custody, OfficeAlerts alerts, OfficeLog log, DateTime now, CancellationToken cancellationToken)
    {
        if (custody.LowBalanceThreshold <= 0)
        {
            return;
        }

        var current = await db.Periods.AsNoTracking()
            .Where(period => period.CustodyId == custody.Id && period.Status == OfficePeriodStatus.Open)
            .Select(period => (int?)period.Id).FirstOrDefaultAsync(cancellationToken);
        var month = current is { } periodId ? (await CustodyPeriods.FiguresAsync(db, [periodId], cancellationToken))[periodId] : OfficePeriodFigures.Zero;
        var low = IsLow(custody, month);

        if (low && custody.LowBalanceWarnedAt is null)
        {
            custody.LowBalanceWarnedAt = now;
            var recipients = (await alerts.ReviewersAsync(null, cancellationToken)).Append(custody.HolderUserId).Distinct().ToList();
            alerts.Add<OfficeLowBalanceAlert>(db, recipients, custody.Id, OfficeAlertSources.Custody, custody.Id, now);
            await db.SaveChangesAsync(cancellationToken);
            log.LowBalance(custody.Id, recipients.Count);
        }
        else if (!low && custody.LowBalanceWarnedAt is not null)
        {
            custody.LowBalanceWarnedAt = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
