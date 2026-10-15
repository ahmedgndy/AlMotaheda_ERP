using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Ledger;
using AlMotaheda.Modules.Workforce.Features.Sheets;
using static AlMotaheda.Modules.Workforce.Tests.Unit.FourteenWorkers;

namespace AlMotaheda.Modules.Workforce.Tests.Unit;

/// <summary>
/// The labor figures the project items screens show, with no database. Only approved, non-void lines count, and a
/// part on one item never shows anywhere else.
/// </summary>
public sealed class LaborItemLedgerTests
{
    private static readonly DateOnly Day = new(2026, 1, 15);

    [Fact]
    public void The_plumbing_item_shows_exactly_the_seven_plumbers_with_their_days_rates_and_cost()
    {
        var (lines, allocations) = ApprovedSheet(FourteenWorkers.Lines);

        var ledger = LaborItemLedger.Compute(lines, allocations);

        var plumbing = ledger.LinesOf(Plumbing);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], plumbing.Select(line => line.WorkerId).Order());
        Assert.All(plumbing, line =>
        {
            Assert.Equal(1m, line.Days);
            Assert.Equal(550m, line.DailyRate);
            Assert.Equal(550m, line.LineNet);
            Assert.Equal(1m, line.Portion);
            Assert.Equal(550m, line.Amount);
            Assert.Equal(0m, line.Paid);
            Assert.Equal(550m, line.Outstanding);
            Assert.Equal(PaymentStates.Unpaid, line.PaymentState);
            Assert.Equal("Plumber", line.TradeNameEn);
        });

        var totals = ledger.Totals.ToDictionary(total => total.ItemId ?? 0);
        Assert.Equal(new ItemCostTotal(Plumbing, 3_850m, 0m, RecordCount: 7, PartyCount: 7), totals[Plumbing]);
        Assert.Equal(new ItemCostTotal(Concrete, 1_875m, 0m, RecordCount: 6, PartyCount: 6), totals[Concrete]);
        Assert.Equal(new ItemCostTotal(Tiling, 1_355m, 0m, RecordCount: 4, PartyCount: 4), totals[Tiling]);
        Assert.False(totals.ContainsKey(0)); // nothing is left not linked

        // The other workers' days land on their own items, and the items add up to the sheet's net.
        Assert.Equal([8, 9, 10, 11, 12, 14], ledger.LinesOf(Concrete).Select(line => line.WorkerId).Order());
        Assert.Equal([11, 12, 13, 14], ledger.LinesOf(Tiling).Select(line => line.WorkerId).Order());
        Assert.Equal(7_080m, ledger.Totals.Sum(total => total.Cost));
        Assert.Equal(7_080m, ledger.Recognized);
        Assert.Equal(14, ledger.RecognizedLines);
    }

    [Fact]
    public void Unapproved_returned_rejected_and_void_lines_are_in_no_total()
    {
        LedgerLineRow[] lines =
        [
            Row(1, workerId: 1, SheetStatus.Approved, isVoid: false, net: 500m),
            Row(2, workerId: 2, SheetStatus.Draft, isVoid: false, net: null),
            Row(3, workerId: 3, SheetStatus.Submitted, isVoid: false, net: null),
            Row(4, workerId: 4, SheetStatus.Returned, isVoid: false, net: null),
            Row(5, workerId: 5, SheetStatus.Rejected, isVoid: true, net: null),
            Row(6, workerId: 6, SheetStatus.Approved, isVoid: true, net: 700m),
        ];
        LedgerAllocationRow[] allocations = [.. lines.Select(line => new LedgerAllocationRow(line.LineId, Plumbing, 1m, line.NetAmount))];

        var ledger = LaborItemLedger.Compute(lines, allocations);

        Assert.Equal(500m, ledger.Recognized);
        Assert.Equal(1, ledger.RecognizedLines);
        Assert.Equal(new ItemCostTotal(Plumbing, 500m, 0m, 1, 1), Assert.Single(ledger.Totals));
        Assert.Equal(1, Assert.Single(ledger.LinesOf(Plumbing)).WorkerId);
        Assert.Empty(ledger.LinesOf(null));
    }

    [Fact]
    public void A_line_allocated_to_item_a_never_shows_in_item_b_or_in_not_linked()
    {
        LedgerLineRow[] lines =
        [
            Row(1, workerId: 1, SheetStatus.Approved, isVoid: false, net: 400m),
            Row(2, workerId: 2, SheetStatus.Approved, isVoid: false, net: 600m),
        ];
        LedgerAllocationRow[] allocations =
        [
            new(1, Plumbing, 1m, 400m),
            new(2, Concrete, 0.5m, 300m),
        ];

        var ledger = LaborItemLedger.Compute(lines, allocations);

        Assert.Equal([1], ledger.LinesOf(Plumbing).Select(line => line.LineId));
        Assert.Equal([2], ledger.LinesOf(Concrete).Select(line => line.LineId));
        var notLinked = Assert.Single(ledger.LinesOf(null));
        Assert.Equal(2, notLinked.LineId);
        Assert.Equal(0.5m, notLinked.Portion);
        Assert.Equal(300m, notLinked.Amount);
        Assert.Equal(1_000m, ledger.Totals.Sum(total => total.Cost));
        Assert.Equal(new ItemCostTotal(null, 300m, 0m, 1, 1), ledger.Totals.Single(total => total.ItemId is null));
    }

    [Fact]
    public void The_paid_shares_of_a_split_partly_paid_line_add_up_to_the_paid_amount()
    {
        LedgerLineRow[] lines = [Row(1, workerId: 1, SheetStatus.Approved, isVoid: false, net: 1_000m, paid: 333.33m)];
        LedgerAllocationRow[] allocations = [new(1, Plumbing, 0.3m, 300m), new(1, Concrete, 0.3m, 300m)];

        var ledger = LaborItemLedger.Compute(lines, allocations);

        var plumbing = Assert.Single(ledger.LinesOf(Plumbing));
        var concrete = Assert.Single(ledger.LinesOf(Concrete));
        var rest = Assert.Single(ledger.LinesOf(null));
        Assert.Equal(100m, plumbing.Paid);
        Assert.Equal(200m, plumbing.Outstanding);
        Assert.Equal(PaymentStates.PartiallyPaid, plumbing.PaymentState);
        Assert.Equal(100m, concrete.Paid);
        Assert.Equal(133.33m, rest.Paid);
        Assert.Equal(333.33m, plumbing.Paid + concrete.Paid + rest.Paid);
        Assert.Equal(333.33m, ledger.Paid);
        Assert.Equal(666.67m, ledger.Outstanding);
        Assert.Equal(333.33m, ledger.Totals.Sum(total => total.Paid));
    }

    [Fact]
    public void Deduction_reasons_and_the_frozen_pay_are_carried_to_the_item_lines()
    {
        LedgerLineRow[] lines = [Row(1, workerId: 1, SheetStatus.Approved, isVoid: false, net: 330m)];
        LedgerAllocationRow[] allocations = [new(1, Plumbing, 1m, 330m)];
        LedgerDeductionRow[] deductions = [new(1, "Late"), new(1, "Broken tool")];

        var line = Assert.Single(LaborItemLedger.Compute(lines, allocations, deductions).LinesOf(Plumbing));

        Assert.Equal("Late; Broken tool", line.DeductionReasons);
        Assert.Equal(330m, line.LineNet);
        Assert.Equal(77, line.RecordedByUserId);
        Assert.Equal("Day", line.Shift);
    }

    [Fact]
    public void No_approved_line_gives_an_empty_ledger()
    {
        var ledger = LaborItemLedger.Compute([Row(1, 1, SheetStatus.Submitted, isVoid: false, net: null)], []);

        Assert.Empty(ledger.Totals);
        Assert.Equal(0m, ledger.Recognized);
        Assert.Equal(0m, ledger.Paid);
        Assert.Equal(0, ledger.RecognizedLines);
    }

    /// <summary>The 14-worker sheet after approval: each allocation has the amount approval gives it.</summary>
    private static (List<LedgerLineRow> Lines, List<LedgerAllocationRow> Allocations) ApprovedSheet(IReadOnlyList<FourteenWorkers.Line> sheet)
    {
        var lines = sheet.Select(line => Row(line.LineId, line.WorkerId, SheetStatus.Approved, isVoid: false, net: line.Net, rate: line.Rate)).ToList();
        var allocations = new List<LedgerAllocationRow>();
        foreach (var line in sheet)
        {
            var amounts = LaborAllocator.Amounts(line.Net, line.Allocations.Select(allocation => allocation.Portion).ToList());
            allocations.AddRange(line.Allocations.Select((allocation, index) => new LedgerAllocationRow(line.LineId, allocation.ItemId, allocation.Portion, amounts[index])));
        }

        return (lines, allocations);
    }

    private static LedgerLineRow Row(int lineId, int workerId, SheetStatus status, bool isVoid, decimal? net, decimal paid = 0m, decimal? rate = null) => new()
    {
        LineId = lineId,
        SheetId = 9,
        Status = status,
        IsVoid = isVoid,
        WorkDate = Day,
        Shift = Shift.Day,
        WorkerId = workerId,
        WorkerNameAr = $"عامل {workerId}",
        WorkerNameEn = $"Worker {workerId:00}",
        TradeId = 10,
        TradeNameAr = "سبّاك",
        TradeNameEn = "Plumber",
        WorkType = WorkTypes.Mep,
        Days = 1m,
        OvertimeHours = 0m,
        DailyRate = net is null ? null : rate ?? net,
        BaseAmount = net,
        OvertimeAmount = net is null ? null : 0m,
        AllowancesAmount = net is null ? null : 0m,
        DeductionsAmount = net is null ? null : 0m,
        NetAmount = net,
        Paid = paid,
        RecordedBy = 77,
    };
}
