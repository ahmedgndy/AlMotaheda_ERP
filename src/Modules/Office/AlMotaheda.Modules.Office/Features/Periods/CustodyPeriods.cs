using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Features.Alerts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Features.Periods;

/// <summary>
/// A month's money, from its own ledger entries only, so every month starts from zero spending:
/// <c>Remaining = Allocated + FundingReceived + CarriedIn − Spent − Returned − CarriedOut + Adjustments</c>.
/// Pending (not posted) and rejected expenses are never in <see cref="Spent"/>.
/// </summary>
/// <param name="Allocated">The month's allocation the custodian received.</param>
/// <param name="FundingReceived">Extra funds (replenishments) received during the month.</param>
/// <param name="CarriedIn">Money an earlier month left, moved here by an authorized carry-forward.</param>
/// <param name="Spent">Approved and posted expenses, less posted reversals.</param>
/// <param name="Returned">Cash handed back to the company from this month.</param>
/// <param name="CarriedOut">What this month left, moved into the next month by an authorized carry-forward.</param>
/// <param name="Adjustments">Reviewed cash-count differences (signed).</param>
/// <param name="Remaining">The month's remaining custody balance.</param>
/// <param name="PendingCount">Expenses of the month recorded but not posted yet (in review, or approved and waiting).</param>
/// <param name="PendingAmount">Their total (not deducted).</param>
internal sealed record OfficePeriodFigures(
    decimal Allocated,
    decimal FundingReceived,
    decimal CarriedIn,
    decimal Spent,
    decimal Returned,
    decimal CarriedOut,
    decimal Adjustments,
    decimal Remaining,
    int PendingCount,
    decimal PendingAmount)
{
    public static readonly OfficePeriodFigures Zero = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The monthly custody cycle. A month opens by itself on its first day (here, the first time anything touches the
/// custody that month, or the background check), the month before it stops being current, and nothing earlier is
/// ever deleted or rewritten. A finished month closes by itself only when nothing is left and nothing waits;
/// otherwise a reviewer settles it (<see cref="OfficePeriodEndpoints"/>).
/// </summary>
internal static class CustodyPeriods
{
    public static (int Year, int Month) MonthOf(DateOnly date) => (date.Year, date.Month);

    public static OfficeCustodyPeriod New(OfficeCustody custody, int year, int month, DateTime now, int? openedBy) => new()
    {
        CustodyId = custody.Id,
        Year = year,
        Month = month,
        StartsOn = new DateOnly(year, month, 1),
        EndsOn = new DateOnly(year, month, DateTime.DaysInMonth(year, month)),
        PlannedAllocation = custody.MonthlyAllocation,
        OpenedAt = now,
        OpenedBy = openedBy,
    };

    /// <summary>
    /// The custody's current month, opened if needed; earlier months still open are ended (and closed when nothing
    /// is left). Must run in a transaction under the custody's lock; saves its changes. <c>null</c> for a closed custody.
    /// </summary>
    public static async Task<OfficeCustodyPeriod?> CurrentAsync(
        OfficeDbContext db, OfficeCustody custody, DateOnly today, DateTime now, OfficeAlerts alerts, OfficeLog log, CancellationToken cancellationToken)
    {
        var (year, month) = MonthOf(today);
        var live = await db.Periods.Where(period => period.CustodyId == custody.Id && period.Status != OfficePeriodStatus.Closed)
            .ToListAsync(cancellationToken);

        var ended = live.Where(period => period.Status == OfficePeriodStatus.Open && period.EndsOn < today).ToList();
        foreach (var period in ended)
        {
            period.MoveTo(OfficePeriodStatus.Closing);
            period.EndedAt = now;
        }

        var current = live.FirstOrDefault(period => period.Year == year && period.Month == month);
        var opened = false;
        if (current is null && custody.Status != OfficeCustodyStatus.Closed)
        {
            current = New(custody, year, month, now, null);
            db.Periods.Add(current);
            opened = true;
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var period in ended)
        {
            log.PeriodMoved(custody.Id, period.Id, period.Status.ToString());
            if (!await TryCloseEmptyAsync(db, period, now, log, cancellationToken))
            {
                alerts.Add<OfficePeriodEndedAlert>(db, await alerts.ReviewersAsync(null, cancellationToken), custody.Id, OfficeAlertSources.Period, period.Id, now);
            }
        }

        if (opened)
        {
            log.PeriodOpened(custody.Id, current!.Id, year, month);
            alerts.Add<OfficePeriodOpenedAlert>(db, [custody.HolderUserId], custody.Id, OfficeAlertSources.Period, current.Id, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return current;
    }

    /// <summary>
    /// The month a record dated <paramref name="date"/> belongs to, if records may still land there: the current
    /// month (opened if needed) or a finished month not settled yet. Must run under the custody's lock.
    /// </summary>
    public static async Task<(OfficeCustodyPeriod? Period, Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? Refused)> ForDateAsync(
        OfficeDbContext db, OfficeCustody custody, DateOnly date, DateOnly today, DateTime now, OfficeAlerts alerts, OfficeLog log, CancellationToken cancellationToken)
    {
        var current = await CurrentAsync(db, custody, today, now, alerts, log, cancellationToken);
        if (current is not null && current.Contains(date))
        {
            return (current, null);
        }

        var (year, month) = MonthOf(date);
        var period = await db.Periods.SingleOrDefaultAsync(candidate => candidate.CustodyId == custody.Id && candidate.Year == year && candidate.Month == month, cancellationToken);
        if (period is null)
        {
            return (null, OfficeProblems.NoMonthFor(date));
        }

        return period.AcceptsRecords ? (period, null) : (null, OfficeProblems.MonthClosed(year, month));
    }

    /// <summary>Closes a finished month by itself when nothing is left and nothing waits. Saves when it closes it.</summary>
    public static async Task<bool> TryCloseEmptyAsync(OfficeDbContext db, OfficeCustodyPeriod period, DateTime now, OfficeLog log, CancellationToken cancellationToken)
    {
        if (period.Status != OfficePeriodStatus.Closing)
        {
            return false;
        }

        var figures = (await FiguresAsync(db, [period.Id], cancellationToken))[period.Id];
        if (figures.Remaining != 0 || await WaitingAsync(db, period.Id, cancellationToken) > 0)
        {
            return false;
        }

        Close(period, OfficePeriodSettlement.NothingLeft, 0m, null, null, now);
        await db.SaveChangesAsync(cancellationToken);
        log.PeriodMoved(period.CustodyId, period.Id, period.Status.ToString());
        return true;
    }

    public static void Close(OfficeCustodyPeriod period, OfficePeriodSettlement settlement, decimal amount, int? closedBy, string? note, DateTime now)
    {
        period.MoveTo(OfficePeriodStatus.Closed);
        period.Settlement = settlement;
        period.SettledAmount = amount;
        period.ClosedAt = now;
        period.ClosedBy = closedBy;
        period.ClosingNote = note;
    }

    /// <summary>
    /// What still waits in a month: its expenses not decided or not posted (a returned one included: it may come
    /// back), and its allocation asked but not received. A month with any of these can't be settled.
    /// </summary>
    public static async Task<int> WaitingAsync(OfficeDbContext db, int periodId, CancellationToken cancellationToken) =>
        await db.Expenses.CountAsync(expense => expense.PeriodId == periodId && expense.PostingStatus == OfficePostingStatus.NotPosted
                                                && expense.Status != OfficeExpenseStatus.Rejected, cancellationToken)
        + await db.FundingRequests.CountAsync(funding => funding.PeriodId == periodId
                                                         && (funding.Status == OfficeFundingStatus.Submitted || funding.Status == OfficeFundingStatus.Approved
                                                             || funding.Status == OfficeFundingStatus.Disbursed), cancellationToken);

    /// <summary>The figures of several months with two queries.</summary>
    public static async Task<IReadOnlyDictionary<int, OfficePeriodFigures>> FiguresAsync(
        OfficeDbContext db, IReadOnlyCollection<int> periodIds, CancellationToken cancellationToken)
    {
        var ids = periodIds.Distinct().ToList();
        var sums = await db.LedgerEntries.AsNoTracking()
            .Where(entry => ids.Contains(entry.PeriodId))
            .GroupBy(entry => new { entry.PeriodId, entry.Kind })
            .Select(group => new { group.Key.PeriodId, group.Key.Kind, Sum = group.Sum(entry => entry.Amount) })
            .ToListAsync(cancellationToken);
        var pending = await db.Expenses.AsNoTracking()
            .Where(expense => ids.Contains(expense.PeriodId) && expense.PostingStatus == OfficePostingStatus.NotPosted
                              && (expense.Status == OfficeExpenseStatus.Submitted || expense.Status == OfficeExpenseStatus.PartlyApproved
                                  || expense.Status == OfficeExpenseStatus.Approved))
            .GroupBy(expense => expense.PeriodId)
            .Select(group => new { group.Key, Count = group.Count(), Sum = group.Sum(expense => expense.Amount) })
            .ToDictionaryAsync(row => row.Key, cancellationToken);

        return ids.ToDictionary(id => id, id =>
        {
            decimal Of(OfficeLedgerKind kind) => sums.Where(row => row.PeriodId == id && row.Kind == kind).Sum(row => row.Sum);
            var waiting = pending.GetValueOrDefault(id);
            return new OfficePeriodFigures(
                Of(OfficeLedgerKind.Allocation),
                Of(OfficeLedgerKind.Funding),
                Of(OfficeLedgerKind.CarryForwardIn),
                -(Of(OfficeLedgerKind.Expense) + Of(OfficeLedgerKind.Reversal)),
                -Of(OfficeLedgerKind.Return),
                -Of(OfficeLedgerKind.CarryForwardOut),
                Of(OfficeLedgerKind.Adjustment),
                sums.Where(row => row.PeriodId == id).Sum(row => row.Sum),
                waiting?.Count ?? 0,
                waiting?.Sum ?? 0m);
        });
    }

    /// <summary>A month's remaining balance (the sum of its entries), read inside the caller's transaction.</summary>
    public static async Task<decimal> RemainingAsync(OfficeDbContext db, int periodId, CancellationToken cancellationToken) =>
        await db.LedgerEntries.Where(entry => entry.PeriodId == periodId).SumAsync(entry => (decimal?)entry.Amount, cancellationToken) ?? 0m;

    /// <summary>The current month of each custody (the open one), if any.</summary>
    public static async Task<IReadOnlyDictionary<int, OfficeCustodyPeriod>> OpenOfAsync(
        OfficeDbContext db, IReadOnlyCollection<int> custodyIds, CancellationToken cancellationToken) =>
        (await db.Periods.AsNoTracking()
            .Where(period => custodyIds.Contains(period.CustodyId) && period.Status == OfficePeriodStatus.Open)
            .ToListAsync(cancellationToken))
        .GroupBy(period => period.CustodyId)
        .ToDictionary(group => group.Key, group => group.OrderByDescending(period => period.StartsOn).First());

    /// <summary>
    /// Opens this month and ends the last one for the custodies whose current month is out of date (no row for
    /// today's month yet, or an older month still open). Each custody in its own transaction and lock. Used before
    /// reads, so a screen opened on the 1st already shows the new month.
    /// </summary>
    public static async Task RollIfNeededAsync(
        OfficeDbContext db, IReadOnlyCollection<int> custodyIds, DateOnly today, DateTime now, OfficeAlerts alerts, OfficeLog log, CancellationToken cancellationToken)
    {
        if (custodyIds.Count == 0 || db.Database.CurrentTransaction is not null)
        {
            return;
        }

        var (year, month) = MonthOf(today);
        var upToDate = await db.Periods.AsNoTracking()
            .Where(period => custodyIds.Contains(period.CustodyId) && period.Year == year && period.Month == month)
            .Select(period => period.CustodyId)
            .ToListAsync(cancellationToken);
        var stale = await db.Periods.AsNoTracking()
            .Where(period => custodyIds.Contains(period.CustodyId) && period.Status == OfficePeriodStatus.Open && period.EndsOn < today)
            .Select(period => period.CustodyId)
            .ToListAsync(cancellationToken);
        var custodies = await db.Custodies.AsNoTracking()
            .Where(custody => custodyIds.Contains(custody.Id) && custody.Status != OfficeCustodyStatus.Closed)
            .Select(custody => custody.Id)
            .ToListAsync(cancellationToken);

        foreach (var custodyId in custodies.Where(id => !upToDate.Contains(id) || stale.Contains(id)))
        {
            await RollOneAsync(db, custodyId, today, now, alerts, log, cancellationToken);
        }
    }

    /// <summary>Rolls one custody in its own transaction. Two callers racing open the month once (the lock, then the unique index).</summary>
    public static async Task RollOneAsync(
        OfficeDbContext db, int custodyId, DateOnly today, DateTime now, OfficeAlerts alerts, OfficeLog log, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedger.LockAsync(db, custodyId, cancellationToken))
        {
            return;
        }

        var custody = await db.Custodies.SingleAsync(candidate => candidate.Id == custodyId, cancellationToken);
        try
        {
            await CurrentAsync(db, custody, today, now, alerts, log, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, Data.Configurations.OfficeCustodyPeriodConfiguration.MonthIndex))
        {
            // Someone else opened it a moment ago.
            await transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }
}
