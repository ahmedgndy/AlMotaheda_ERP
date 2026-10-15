using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>An allowance or deduction of a line. The amount is shown with workers See money, and always to the recorder who typed it.</summary>
internal sealed record LaborAdjustmentView(int Id, string Kind, string Type, decimal? Amount, string? Reason);

/// <summary>A part of a line's day on a project item. The amount exists once the sheet is approved.</summary>
internal sealed record LaborAllocationView(int Id, int ItemId, string? ItemNameAr, string? ItemNameEn, decimal Portion, decimal? Amount);

/// <summary>
/// One worker's day on a sheet. Pay fields are <c>null</c> without workers See money. Before approval they are a
/// preview with today's rates (<see cref="LaborSheetView.PayIsPreview"/>); after approval they are the frozen pay.
/// </summary>
/// <param name="UnallocatedPortion">The part of the day linked to no item.</param>
internal sealed record LaborLineView(
    int Id,
    int WorkerId,
    string WorkerNameAr,
    string WorkerNameEn,
    int TradeId,
    string TradeNameAr,
    string TradeNameEn,
    decimal Days,
    decimal OvertimeHours,
    string WorkType,
    string? Notes,
    bool IsVoid,
    decimal? DailyRate,
    decimal? BaseAmount,
    decimal? OvertimeAmount,
    decimal? AllowancesAmount,
    decimal? DeductionsAmount,
    decimal? NetAmount,
    decimal? Paid,
    decimal? Outstanding,
    string? PaymentState,
    IReadOnlyList<LaborAdjustmentView> Adjustments,
    IReadOnlyList<LaborAllocationView> Allocations,
    decimal UnallocatedPortion,
    decimal? UnallocatedAmount);

/// <summary>The sheet's totals. Money is <c>null</c> without workers See money.</summary>
internal sealed record LaborSheetTotals(
    int LineCount,
    decimal WorkerDays,
    decimal OvertimeHours,
    decimal? BaseAmount,
    decimal? OvertimeAmount,
    decimal? AllowancesAmount,
    decimal? DeductionsAmount,
    decimal? NetAmount,
    decimal? Paid);

/// <summary>One labor sheet with its lines, as the API returns it.</summary>
/// <param name="PayIsPreview">True before approval: the pay is computed with today's rates and can still change.</param>
internal sealed record LaborSheetView(
    int Id,
    int ProjectId,
    DateOnly WorkDate,
    string Shift,
    string Status,
    int RecordedBy,
    string? RecordedByNameAr,
    string? RecordedByNameEn,
    DateTime? SubmittedAt,
    DateTime? ReviewedAt,
    int? ReviewedBy,
    string? ReviewNote,
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    bool PayIsPreview,
    LaborSheetTotals Totals,
    IReadOnlyList<LaborLineView> Lines,
    string RowVersion)
{
    /// <summary>Loads one sheet and builds its view, or <c>null</c> when it isn't on this project.</summary>
    public static async Task<LaborSheetView?> LoadAsync(
        WorkforceDbContext db,
        IProjectItems projectItems,
        IUserDirectory users,
        WorkforceCaller caller,
        LaborPaySettings settings,
        int projectId,
        int sheetId,
        CancellationToken cancellationToken)
    {
        var row = await SheetQueries.One(db, projectId, sheetId).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var itemIds = row.Lines.SelectMany(line => line.Allocations).Select(allocation => allocation.ItemId).Distinct().ToArray();
        var items = await projectItems.FindAsync(projectId, itemIds, cancellationToken);
        var people = await users.FindAsync([row.RecordedBy], cancellationToken);

        return From(row, items, people.GetValueOrDefault(row.RecordedBy), caller, settings);
    }

    public static LaborSheetView From(
        SheetRow row,
        IReadOnlyDictionary<int, ProjectItemRef> items,
        UserSummary? recorder,
        WorkforceCaller caller,
        LaborPaySettings settings)
    {
        var seesMoney = caller.SeesMoney;
        var seesAdjustments = seesMoney || caller.UserId == row.RecordedBy;
        var approved = row.Status == SheetStatus.Approved;

        // A rejected sheet's lines are void: they have no pay, not even a preview.
        var lines = row.Lines
            .Select(line => LineView(line, items, approved, row.Status != SheetStatus.Rejected && seesMoney, seesAdjustments, settings))
            .ToList();

        var withPay = lines.Where(line => line.NetAmount is not null).ToList();
        var showTotals = seesMoney && row.Status != SheetStatus.Rejected;

        var totals = new LaborSheetTotals(
            lines.Count,
            lines.Sum(line => line.Days),
            lines.Sum(line => line.OvertimeHours),
            showTotals ? withPay.Sum(line => line.BaseAmount!.Value) : null,
            showTotals ? withPay.Sum(line => line.OvertimeAmount!.Value) : null,
            showTotals ? withPay.Sum(line => line.AllowancesAmount!.Value) : null,
            showTotals ? withPay.Sum(line => line.DeductionsAmount!.Value) : null,
            showTotals ? withPay.Sum(line => line.NetAmount!.Value) : null,
            showTotals && approved ? withPay.Sum(line => line.Paid ?? 0m) : null);

        return new LaborSheetView(
            row.Id,
            row.ProjectId,
            row.WorkDate,
            row.Shift.ToString(),
            row.Status.ToString(),
            row.RecordedBy,
            recorder?.NameAr,
            recorder?.NameEn,
            row.SubmittedAt,
            row.ReviewedAt,
            row.ReviewedBy,
            row.ReviewNote,
            row.Notes,
            row.CreatedAt,
            row.UpdatedAt,
            !approved,
            totals,
            lines,
            RowVersions.Format(row.RowVersion));
    }

    private static LaborLineView LineView(
        SheetLineRow line,
        IReadOnlyDictionary<int, ProjectItemRef> items,
        bool approved,
        bool showPay,
        bool showAdjustments,
        LaborPaySettings settings)
    {
        // Approved: the frozen pay. Before: the same arithmetic with today's rates.
        var pay = approved && line.NetAmount is not null
            ? new LinePay(line.AppliedDailyRate!.Value, line.BaseAmount!.Value, line.OvertimeAmount!.Value,
                line.AllowancesAmount!.Value, line.DeductionsAmount!.Value, line.NetAmount.Value)
            : LaborPay.Compute(line.Rate, line.Days, line.OvertimeHours, line.PayAdjustments, settings);

        var paid = approved ? Math.Min(line.Paid, pay.NetAmount) : 0m;
        var portions = line.Allocations.Select(allocation => allocation.Portion).ToList();
        var unallocatedPortion = LaborAllocator.UnallocatedPortion(portions);
        var amounts = approved ? line.Allocations.Select(allocation => allocation.Amount).ToList() : line.Allocations.Select(_ => (decimal?)null).ToList();

        return new LaborLineView(
            line.Id,
            line.WorkerId,
            line.WorkerNameAr,
            line.WorkerNameEn,
            line.TradeId,
            line.TradeNameAr,
            line.TradeNameEn,
            line.Days,
            line.OvertimeHours,
            line.WorkType,
            line.Notes,
            line.IsVoid,
            showPay ? pay.DailyRate : null,
            showPay ? pay.BaseAmount : null,
            showPay ? pay.OvertimeAmount : null,
            showPay ? pay.AllowancesAmount : null,
            showPay ? pay.DeductionsAmount : null,
            showPay ? pay.NetAmount : null,
            showPay && approved ? paid : null,
            showPay && approved ? pay.NetAmount - paid : null,
            showPay && approved ? PaymentStates.Of(pay.NetAmount, paid) : null,
            line.Adjustments
                .Select(adjustment => new LaborAdjustmentView(
                    adjustment.Id,
                    adjustment.Kind.ToString(),
                    adjustment.Type.ToString(),
                    showAdjustments ? adjustment.Amount : null,
                    adjustment.Reason))
                .ToList(),
            line.Allocations
                .Select((allocation, index) => new LaborAllocationView(
                    allocation.Id,
                    allocation.ItemId,
                    items.GetValueOrDefault(allocation.ItemId)?.NameAr,
                    items.GetValueOrDefault(allocation.ItemId)?.NameEn,
                    allocation.Portion,
                    showPay ? amounts[index] : null))
                .ToList(),
            unallocatedPortion,
            showPay && approved && unallocatedPortion > 0 ? pay.NetAmount - amounts.Sum(amount => amount ?? 0m) : null);
    }
}
