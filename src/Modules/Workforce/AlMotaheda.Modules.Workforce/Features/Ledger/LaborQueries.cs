using AlMotaheda.Modules.Workforce.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Ledger;

/// <summary>A line waiting for review, with what its pay preview needs.</summary>
internal sealed record PendingLineRow(
    int LineId,
    int LineTradeId,
    int WorkerTradeId,
    decimal LineTradeDefaultRate,
    decimal? AssignmentRate,
    decimal? WorkerRate,
    decimal Days,
    decimal OvertimeHours,
    decimal Allowances,
    decimal Deductions);

/// <summary>
/// The flat queries behind the labor ledger, one per kind of row (decision G5): the handler loads them and
/// <see cref="LaborItemLedger"/> does the arithmetic. As <see cref="IQueryable{T}"/> so tests can prove they translate.
/// </summary>
internal static class LaborQueries
{
    /// <summary>The approved, non-void lines of a project (optionally within work dates), with what is paid on each.</summary>
    public static IQueryable<LedgerLineRow> LedgerLines(WorkforceDbContext db, int projectId, DateOnly? from, DateOnly? to) =>
        Approved(db, projectId, from, to)
            .Select(line => new LedgerLineRow
            {
                LineId = line.Id,
                SheetId = line.SheetId,
                Status = line.Sheet.Status,
                IsVoid = line.IsVoid,
                WorkDate = line.WorkDate,
                Shift = line.Shift,
                WorkerId = line.WorkerId,
                WorkerNameAr = line.Worker.NameAr,
                WorkerNameEn = line.Worker.NameEn,
                TradeId = line.TradeId,
                TradeNameAr = line.Trade.NameAr,
                TradeNameEn = line.Trade.NameEn,
                WorkType = line.WorkType,
                Days = line.Days,
                OvertimeHours = line.OvertimeHours,
                DailyRate = line.AppliedDailyRate,
                BaseAmount = line.BaseAmount,
                OvertimeAmount = line.OvertimeAmount,
                AllowancesAmount = line.AllowancesAmount,
                DeductionsAmount = line.DeductionsAmount,
                NetAmount = line.NetAmount,
                Paid = db.WorkerPaymentAllocations
                    .Where(allocation => allocation.LineId == line.Id && allocation.Payment.VoidedAt == null)
                    .Sum(allocation => allocation.Amount),
                RecordedBy = line.Sheet.RecordedBy,
            });

    /// <summary>The item allocations of those same lines.</summary>
    public static IQueryable<LedgerAllocationRow> LedgerAllocations(WorkforceDbContext db, int projectId, DateOnly? from, DateOnly? to) =>
        Approved(db, projectId, from, to)
            .SelectMany(line => line.Allocations)
            .Select(allocation => new LedgerAllocationRow(allocation.LineId, allocation.ProjectItemId, allocation.Portion, allocation.Amount));

    /// <summary>The deduction reasons of those same lines, for the items screens.</summary>
    public static IQueryable<LedgerDeductionRow> LedgerDeductions(WorkforceDbContext db, int projectId) =>
        Approved(db, projectId, null, null)
            .SelectMany(line => line.Adjustments)
            .Where(adjustment => adjustment.Kind == AdjustmentKind.Deduction)
            .OrderBy(adjustment => adjustment.Id)
            .Select(adjustment => new LedgerDeductionRow(adjustment.LineId, adjustment.Reason));

    /// <summary>
    /// The lines of a project waiting for a reviewer (on submitted sheets, not void), with the rate inputs and the
    /// adjustment sums a pay preview needs. A returned sheet is back with its recorder, so it is not pending review;
    /// the other cost groups count the same way. Each line is one record (as in <c>RecognizedRecords</c>).
    /// </summary>
    public static IQueryable<PendingLineRow> PendingLines(WorkforceDbContext db, int projectId, DateOnly? from, DateOnly? to)
    {
        var query = db.AttendanceLines.AsNoTracking()
            .Where(line => line.Sheet.ProjectId == projectId && !line.IsVoid && line.Sheet.Status == SheetStatus.Submitted);

        query = WithinDates(query, from, to);

        return query.Select(line => new PendingLineRow(
            line.Id,
            line.TradeId,
            line.Worker.TradeId,
            line.Trade.DefaultDailyRate,
            db.WorkerAssignments
                .Where(assignment => assignment.WorkerId == line.WorkerId && assignment.ProjectId == projectId
                    && assignment.StartDate <= line.WorkDate
                    && (assignment.EndDate == null || assignment.EndDate >= line.WorkDate))
                .OrderByDescending(assignment => assignment.StartDate)
                .Select(assignment => assignment.DailyRate)
                .FirstOrDefault(),
            line.Worker.DailyRate,
            line.Days,
            line.OvertimeHours,
            line.Adjustments.Where(adjustment => adjustment.Kind == AdjustmentKind.Allowance).Sum(adjustment => adjustment.Amount),
            line.Adjustments.Where(adjustment => adjustment.Kind == AdjustmentKind.Deduction).Sum(adjustment => adjustment.Amount)));
    }

    /// <summary>Any labor allocation, approved or not, on the item. Such an item can't be deleted.</summary>
    public static IQueryable<LaborAllocation> LinksToItem(WorkforceDbContext db, int projectId, int itemId) =>
        db.LaborAllocations.AsNoTracking()
            .Where(allocation => allocation.ProjectItemId == itemId && allocation.Line.Sheet.ProjectId == projectId);

    /// <summary>How many sheets of the project are in each status.</summary>
    public static IQueryable<SheetStatusCount> SheetCounts(WorkforceDbContext db, int projectId) =>
        db.DailySheets.AsNoTracking()
            .Where(sheet => sheet.ProjectId == projectId)
            .GroupBy(sheet => sheet.Status)
            .Select(group => new SheetStatusCount(group.Key, group.Count()));

    /// <summary>How many workers are on the project now (open assignments).</summary>
    public static IQueryable<WorkerAssignment> OpenAssignments(WorkforceDbContext db, int projectId) =>
        db.WorkerAssignments.AsNoTracking().Where(assignment => assignment.ProjectId == projectId && assignment.EndDate == null);

    private static IQueryable<AttendanceLine> Approved(WorkforceDbContext db, int projectId, DateOnly? from, DateOnly? to) =>
        WithinDates(
            db.AttendanceLines.AsNoTracking()
                .Where(line => line.Sheet.ProjectId == projectId && line.Sheet.Status == SheetStatus.Approved && !line.IsVoid),
            from,
            to);

    private static IQueryable<AttendanceLine> WithinDates(IQueryable<AttendanceLine> query, DateOnly? from, DateOnly? to)
    {
        if (from is { } start)
        {
            query = query.Where(line => line.WorkDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(line => line.WorkDate <= end);
        }

        return query;
    }
}

internal sealed record SheetStatusCount(SheetStatus Status, int Count);
