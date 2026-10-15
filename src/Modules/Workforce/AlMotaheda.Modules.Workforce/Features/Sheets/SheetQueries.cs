using AlMotaheda.Modules.Workforce.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>The rate inputs of one line, as <see cref="SheetQueries.RateRows"/> reads them.</summary>
internal sealed record LineRateRow(int LineId, int LineTradeId, int WorkerTradeId, decimal LineTradeDefaultRate, decimal? AssignmentRate, decimal? WorkerRate)
{
    public RateInputs Rate => new(LineTradeId, WorkerTradeId, LineTradeDefaultRate, AssignmentRate, WorkerRate);
}

internal sealed record SheetAdjustmentRow(int Id, AdjustmentKind Kind, AdjustmentType Type, decimal Amount, string? Reason);

internal sealed record SheetAllocationRow(int Id, int ItemId, decimal Portion, decimal? Amount);

/// <summary>One line of a sheet with what its view and its pay preview need.</summary>
internal sealed class SheetLineRow
{
    public int Id { get; init; }
    public int WorkerId { get; init; }
    public string WorkerNameAr { get; init; } = "";
    public string WorkerNameEn { get; init; } = "";
    public int WorkerTradeId { get; init; }
    public decimal? WorkerRate { get; init; }
    public int TradeId { get; init; }
    public string TradeNameAr { get; init; } = "";
    public string TradeNameEn { get; init; } = "";
    public decimal TradeDefaultRate { get; init; }

    /// <summary>The rate of the assignment that covers the day on this project (<c>null</c>: none, or no project rate).</summary>
    public decimal? AssignmentRate { get; init; }

    public decimal Days { get; init; }
    public decimal OvertimeHours { get; init; }
    public string WorkType { get; init; } = "";
    public string? Notes { get; init; }
    public bool IsVoid { get; init; }
    public decimal? AppliedDailyRate { get; init; }
    public decimal? BaseAmount { get; init; }
    public decimal? OvertimeAmount { get; init; }
    public decimal? AllowancesAmount { get; init; }
    public decimal? DeductionsAmount { get; init; }
    public decimal? NetAmount { get; init; }

    /// <summary>Allocations of worker payments that are not voided.</summary>
    public decimal Paid { get; init; }

    public List<SheetAdjustmentRow> Adjustments { get; init; } = [];
    public List<SheetAllocationRow> Allocations { get; init; } = [];

    public RateInputs Rate => new(TradeId, WorkerTradeId, TradeDefaultRate, AssignmentRate, WorkerRate);

    public IEnumerable<PayAdjustment> PayAdjustments => Adjustments.Select(adjustment => new PayAdjustment(adjustment.Kind, adjustment.Amount));
}

/// <summary>One sheet with its lines, as the queries project it; the view is built from it in memory.</summary>
internal sealed class SheetRow
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public DateOnly WorkDate { get; init; }
    public Shift Shift { get; init; }
    public int RecordedBy { get; init; }
    public SheetStatus Status { get; init; }
    public DateTime? SubmittedAt { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public int? ReviewedBy { get; init; }
    public string? ReviewNote { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public List<SheetLineRow> Lines { get; init; } = [];
}

/// <summary>One sheet in the list: counts, and the approved net (only approved sheets have one).</summary>
internal sealed class SheetListRow
{
    public int Id { get; init; }
    public DateOnly WorkDate { get; init; }
    public Shift Shift { get; init; }
    public int RecordedBy { get; init; }
    public SheetStatus Status { get; init; }
    public DateTime? SubmittedAt { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public int? ReviewedBy { get; init; }
    public int LineCount { get; init; }
    public decimal WorkerDays { get; init; }
    public decimal? ApprovedNet { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

/// <summary>The filters of <c>GET /api/projects/{pid}/labor/sheets</c>, after validation.</summary>
internal sealed record SheetFilter(SheetStatus? Status, DateOnly? From, DateOnly? To, Shift? Shift, int? RecordedBy);

/// <summary>The sheet queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class SheetQueries
{
    /// <summary>The sheets list: every filter runs in SQL; newest day first.</summary>
    public static IQueryable<SheetListRow> List(WorkforceDbContext db, int projectId, SheetFilter filter)
    {
        var query = db.DailySheets.AsNoTracking().Where(sheet => sheet.ProjectId == projectId);

        if (filter.Status is { } status)
        {
            query = query.Where(sheet => sheet.Status == status);
        }

        if (filter.From is { } from)
        {
            query = query.Where(sheet => sheet.WorkDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(sheet => sheet.WorkDate <= to);
        }

        if (filter.Shift is { } shift)
        {
            query = query.Where(sheet => sheet.Shift == shift);
        }

        if (filter.RecordedBy is { } recorder)
        {
            query = query.Where(sheet => sheet.RecordedBy == recorder);
        }

        return query
            .OrderByDescending(sheet => sheet.WorkDate)
            .ThenBy(sheet => sheet.Shift)
            .ThenByDescending(sheet => sheet.Id)
            .Select(sheet => new SheetListRow
            {
                Id = sheet.Id,
                WorkDate = sheet.WorkDate,
                Shift = sheet.Shift,
                RecordedBy = sheet.RecordedBy,
                Status = sheet.Status,
                SubmittedAt = sheet.SubmittedAt,
                ReviewedAt = sheet.ReviewedAt,
                ReviewedBy = sheet.ReviewedBy,
                LineCount = sheet.Lines.Count(),
                WorkerDays = sheet.Lines.Sum(line => line.Days),
                ApprovedNet = sheet.Status == SheetStatus.Approved ? sheet.Lines.Sum(line => line.NetAmount) : null,
                RowVersion = sheet.RowVersion,
            });
    }

    /// <summary>One sheet of a project with its lines, or none when the id belongs to another project.</summary>
    public static IQueryable<SheetRow> One(WorkforceDbContext db, int projectId, int sheetId) =>
        db.DailySheets.AsNoTracking()
            .Where(sheet => sheet.ProjectId == projectId && sheet.Id == sheetId)
            .Select(sheet => new SheetRow
            {
                Id = sheet.Id,
                ProjectId = sheet.ProjectId,
                WorkDate = sheet.WorkDate,
                Shift = sheet.Shift,
                RecordedBy = sheet.RecordedBy,
                Status = sheet.Status,
                SubmittedAt = sheet.SubmittedAt,
                ReviewedAt = sheet.ReviewedAt,
                ReviewedBy = sheet.ReviewedBy,
                ReviewNote = sheet.ReviewNote,
                Notes = sheet.Notes,
                CreatedAt = sheet.CreatedAt,
                UpdatedAt = sheet.UpdatedAt,
                UpdatedBy = sheet.UpdatedBy,
                RowVersion = sheet.RowVersion,
                Lines = sheet.Lines
                    .OrderBy(line => line.Worker.NameEn)
                    .ThenBy(line => line.Id)
                    .Select(line => new SheetLineRow
                    {
                        Id = line.Id,
                        WorkerId = line.WorkerId,
                        WorkerNameAr = line.Worker.NameAr,
                        WorkerNameEn = line.Worker.NameEn,
                        WorkerTradeId = line.Worker.TradeId,
                        WorkerRate = line.Worker.DailyRate,
                        TradeId = line.TradeId,
                        TradeNameAr = line.Trade.NameAr,
                        TradeNameEn = line.Trade.NameEn,
                        TradeDefaultRate = line.Trade.DefaultDailyRate,
                        AssignmentRate = db.WorkerAssignments
                            .Where(assignment => assignment.WorkerId == line.WorkerId && assignment.ProjectId == sheet.ProjectId
                                && assignment.StartDate <= line.WorkDate
                                && (assignment.EndDate == null || assignment.EndDate >= line.WorkDate))
                            .OrderByDescending(assignment => assignment.StartDate)
                            .Select(assignment => assignment.DailyRate)
                            .FirstOrDefault(),
                        Days = line.Days,
                        OvertimeHours = line.OvertimeHours,
                        WorkType = line.WorkType,
                        Notes = line.Notes,
                        IsVoid = line.IsVoid,
                        AppliedDailyRate = line.AppliedDailyRate,
                        BaseAmount = line.BaseAmount,
                        OvertimeAmount = line.OvertimeAmount,
                        AllowancesAmount = line.AllowancesAmount,
                        DeductionsAmount = line.DeductionsAmount,
                        NetAmount = line.NetAmount,
                        Paid = db.WorkerPaymentAllocations
                            .Where(allocation => allocation.LineId == line.Id && allocation.Payment.VoidedAt == null)
                            .Sum(allocation => allocation.Amount),
                        Adjustments = line.Adjustments
                            .OrderBy(adjustment => adjustment.Id)
                            .Select(adjustment => new SheetAdjustmentRow(adjustment.Id, adjustment.Kind, adjustment.Type, adjustment.Amount, adjustment.Reason))
                            .ToList(),
                        Allocations = line.Allocations
                            .OrderBy(allocation => allocation.ProjectItemId)
                            .Select(allocation => new SheetAllocationRow(allocation.Id, allocation.ProjectItemId, allocation.Portion, allocation.Amount))
                            .ToList(),
                    })
                    .ToList(),
            });

    /// <summary>
    /// What decides the rate of each line of a sheet, read at approval: the trade worked, the worker's own trade and
    /// rate, and the rate of the assignment that covers the day on this project.
    /// </summary>
    public static IQueryable<LineRateRow> RateRows(WorkforceDbContext db, int sheetId) =>
        db.AttendanceLines.AsNoTracking()
            .Where(line => line.SheetId == sheetId)
            .Select(line => new LineRateRow(
                line.Id,
                line.TradeId,
                line.Worker.TradeId,
                line.Trade.DefaultDailyRate,
                db.WorkerAssignments
                    .Where(assignment => assignment.WorkerId == line.WorkerId && assignment.ProjectId == line.Sheet.ProjectId
                        && assignment.StartDate <= line.WorkDate
                        && (assignment.EndDate == null || assignment.EndDate >= line.WorkDate))
                    .OrderByDescending(assignment => assignment.StartDate)
                    .Select(assignment => assignment.DailyRate)
                    .FirstOrDefault(),
                line.Worker.DailyRate));

    /// <summary>The project and status of the sheet a line is on, for the line-level allocation endpoint.</summary>
    public static IQueryable<AttendanceLine> LineOfProject(WorkforceDbContext db, int projectId, int lineId) =>
        db.AttendanceLines.Where(line => line.Id == lineId && line.Sheet.ProjectId == projectId);
}
