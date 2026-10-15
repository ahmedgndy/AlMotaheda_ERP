using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Sheets;

namespace AlMotaheda.Modules.Workforce.Features.Ledger;

/// <summary>One labor line, flat, as the ledger needs it. Loaded by <see cref="LaborQueries.LedgerLines"/>.</summary>
internal sealed class LedgerLineRow
{
    public int LineId { get; init; }
    public int SheetId { get; init; }
    public SheetStatus Status { get; init; }
    public bool IsVoid { get; init; }
    public DateOnly WorkDate { get; init; }
    public Shift Shift { get; init; }
    public int WorkerId { get; init; }
    public string WorkerNameAr { get; init; } = "";
    public string WorkerNameEn { get; init; } = "";
    public int TradeId { get; init; }
    public string TradeNameAr { get; init; } = "";
    public string TradeNameEn { get; init; } = "";
    public string WorkType { get; init; } = "";
    public decimal Days { get; init; }
    public decimal OvertimeHours { get; init; }
    public decimal? DailyRate { get; init; }
    public decimal? BaseAmount { get; init; }
    public decimal? OvertimeAmount { get; init; }
    public decimal? AllowancesAmount { get; init; }
    public decimal? DeductionsAmount { get; init; }
    public decimal? NetAmount { get; init; }

    /// <summary>The allocations of payments that are not voided.</summary>
    public decimal Paid { get; init; }

    public int RecordedBy { get; init; }

    /// <summary>Only an approved line that isn't void is labor cost.</summary>
    public bool IsCost => Status == SheetStatus.Approved && !IsVoid && NetAmount is not null;
}

/// <summary>One item allocation of a line, flat.</summary>
internal sealed record LedgerAllocationRow(int LineId, int ItemId, decimal Portion, decimal? Amount);

/// <summary>A deduction reason of a line, for the items screens.</summary>
internal sealed record LedgerDeductionRow(int LineId, string? Reason);

/// <summary>One part of a line on an item (or not linked), as the items screens show it.</summary>
internal sealed record LedgerPart(int? ItemId, LaborItemLine Line);

/// <summary>The labor figures of one project, computed once per request.</summary>
/// <param name="Parts">Every part of every approved line, newest day first.</param>
/// <param name="Totals">One total per item with labor, plus one with a <c>null</c> item for the not-linked parts.</param>
/// <param name="Recognized">Approved labor cost: the sum of the approved lines' net pay.</param>
/// <param name="Paid">The part of <paramref name="Recognized"/> settled by worker payments that are not voided.</param>
/// <param name="RecognizedLines">How many approved lines.</param>
internal sealed record LaborLedger(
    IReadOnlyList<LedgerPart> Parts,
    IReadOnlyList<ItemCostTotal> Totals,
    decimal Recognized,
    decimal Paid,
    int RecognizedLines)
{
    public static LaborLedger Empty { get; } = new([], [], 0m, 0m, 0);

    public decimal Outstanding => Recognized - Paid;

    /// <summary>The parts on one item, or the not-linked parts when <paramref name="itemId"/> is <c>null</c>.</summary>
    public IReadOnlyList<LaborItemLine> LinesOf(int? itemId) =>
        Parts.Where(part => part.ItemId == itemId).Select(part => part.Line).ToList();
}

/// <summary>
/// The labor of a project by project item, with no database access (decision G5). The items screens, the overview
/// and the labor summary all read it, so they can't disagree.
/// </summary>
/// <remarks>
/// Only approved lines that aren't void count. Each counted line is split by <see cref="LaborAllocator.Split"/>
/// into its parts: a part on an item never shows on another item or in "not linked", and the parts of a line add
/// up to its net and to what was paid on it.
/// </remarks>
internal static class LaborItemLedger
{
    public static LaborLedger Compute(
        IReadOnlyList<LedgerLineRow> lines,
        IReadOnlyList<LedgerAllocationRow> allocations,
        IReadOnlyList<LedgerDeductionRow>? deductions = null)
    {
        var counted = lines.Where(line => line.IsCost).ToList();
        if (counted.Count == 0)
        {
            return LaborLedger.Empty;
        }

        var allocationsByLine = allocations
            .GroupBy(allocation => allocation.LineId)
            .ToDictionary(group => group.Key, group => group.OrderBy(allocation => allocation.ItemId).ToList());

        var reasonsByLine = (deductions ?? [])
            .Where(deduction => deduction.Reason is not null)
            .GroupBy(deduction => deduction.LineId)
            .ToDictionary(group => group.Key, group => string.Join("; ", group.Select(deduction => deduction.Reason)));

        var parts = new List<LedgerPart>();
        foreach (var line in counted
                     .OrderByDescending(line => line.WorkDate)
                     .ThenBy(line => line.Shift)
                     .ThenBy(line => line.WorkerNameEn, StringComparer.Ordinal)
                     .ThenBy(line => line.LineId))
        {
            var net = line.NetAmount!.Value;
            var paid = Math.Min(line.Paid, net);
            var lineAllocations = allocationsByLine.TryGetValue(line.LineId, out var found)
                ? found.Select(allocation => (allocation.ItemId, allocation.Portion, allocation.Amount)).ToList()
                : [];

            foreach (var part in LaborAllocator.Split(net, paid, lineAllocations))
            {
                parts.Add(new LedgerPart(part.ItemId, ToItemLine(line, part, reasonsByLine.GetValueOrDefault(line.LineId))));
            }
        }

        var totals = parts
            .GroupBy(part => part.ItemId)
            .OrderBy(group => group.Key is null)
            .ThenBy(group => group.Key)
            .Select(group => new ItemCostTotal(
                group.Key,
                group.Sum(part => part.Line.Amount),
                group.Sum(part => part.Line.Paid),
                group.Count(),
                group.Select(part => part.Line.WorkerId).Distinct().Count()))
            .ToList();

        return new LaborLedger(
            parts,
            totals,
            counted.Sum(line => line.NetAmount!.Value),
            counted.Sum(line => Math.Min(line.Paid, line.NetAmount!.Value)),
            counted.Count);
    }

    private static LaborItemLine ToItemLine(LedgerLineRow line, LinePart part, string? deductionReasons) => new(
        line.LineId,
        line.SheetId,
        line.WorkDate,
        line.Shift.ToString(),
        line.WorkerId,
        line.WorkerNameAr,
        line.WorkerNameEn,
        line.TradeId,
        line.TradeNameAr,
        line.TradeNameEn,
        line.WorkType,
        line.Days,
        line.OvertimeHours,
        line.DailyRate ?? 0m,
        line.BaseAmount ?? 0m,
        line.OvertimeAmount ?? 0m,
        line.AllowancesAmount ?? 0m,
        line.DeductionsAmount ?? 0m,
        deductionReasons,
        line.NetAmount ?? 0m,
        part.Portion,
        part.Amount,
        part.Paid,
        part.Amount - part.Paid,
        PaymentStates.Of(part.Amount, part.Paid),
        line.RecordedBy);
}
