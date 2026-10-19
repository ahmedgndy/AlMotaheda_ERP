using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Features.Items;

namespace AlMotaheda.Modules.Projects.Tests.Unit.CostManagement;

/// <summary>The work-item breakdown arithmetic: supplier liabilities and payments, labor, subcontract commitments, budget.</summary>
public sealed class ItemCostBreakdownTests
{
    private const int Concrete = 1;
    private const int Cement = 2;

    [Fact]
    public void Ten_workers_at_500_a_day_cost_5000_on_the_item()
    {
        var lines = Enumerable.Range(1, 10).Select(worker => LaborLine(worker, dailyRate: 500m)).ToList();

        var rows = ItemCostBreakdown.Filter(ItemCostBreakdown.FromLabor(new Dictionary<int, IReadOnlyList<LaborItemLine>> { [Concrete] = lines }), BreakdownFilter.None);
        var totals = ItemCostBreakdown.Totals(rows);
        var workers = ItemCostBreakdown.Workers(rows);

        Assert.Equal(10, totals.RecordCount);
        Assert.Equal(5_000m, totals.Amount);
        Assert.Equal(5_000m, totals.ByType.Single(type => type.Type == BreakdownTypes.Labor).Amount);
        Assert.Equal(10, workers.Count);
        Assert.All(workers, worker => Assert.Equal(500m, worker.Cost));
        Assert.Equal(10m, workers.Sum(worker => worker.Days));
    }

    [Fact]
    public void A_supplier_owes_only_its_invoices_less_their_allocations_and_custody_is_paid_at_once()
    {
        var rows = new[]
        {
            Supply(1, supplierId: 7, quantity: 2m, unitPrice: 1_400m, channel: "Credit", paid: 1_000m),
            Supply(2, supplierId: 7, quantity: 1m, unitPrice: 1_100m, channel: "Custody", paid: 1_100m),
            Supply(3, supplierId: 8, quantity: 5m, unitPrice: 100m, channel: "Credit", paid: 0m),
        };

        var suppliers = ItemCostBreakdown.Suppliers(rows);

        var cemex = suppliers.Single(supplier => supplier.SupplierId == 7);
        Assert.Equal(2_800m, cemex.Invoiced);
        Assert.Equal(1_100m, cemex.CustodyPaid);
        Assert.Equal(2_100m, cemex.Paid);
        Assert.Equal(1_800m, cemex.Outstanding);
        Assert.Equal([new UnitQuantity("Ton", 3m)], cemex.Quantities);
        Assert.Equal(500m, suppliers.Single(supplier => supplier.SupplierId == 8).Outstanding);
    }

    [Fact]
    public void A_subcontract_commitment_is_shown_next_to_its_work_and_never_added_to_cost()
    {
        var work = new Dictionary<int, IReadOnlyList<SubcontractItemLine>>
        {
            [Concrete] = [new SubcontractItemLine("Measurement", 50, 9, "SC-1", 3, "مقاول", "Sub Co", "Concrete", 70, "Slab", new DateOnly(2026, 1, 9),
                40m, "M3", 1_000m, 40_000m, 10_000m, 30_000m, PaymentStates.PartiallyPaid)],
        };
        var commitments = new[] { new SubcontractItemCommitment(Concrete, 9, "SC-1", 70, "Slab", 3, "مقاول", "Sub Co", "Active", 100_000m, 40_000m, 60_000m) };

        var rows = ItemCostBreakdown.Filter(ItemCostBreakdown.FromSubcontracts(work), BreakdownFilter.None);
        var subcontractor = Assert.Single(ItemCostBreakdown.Subcontractors(rows, commitments));

        Assert.Equal(40_000m, ItemCostBreakdown.Totals(rows).Amount);
        Assert.Equal(40_000m, subcontractor.Recognized);
        Assert.Equal(10_000m, subcontractor.Paid);
        Assert.Equal(30_000m, subcontractor.Outstanding);
        Assert.Equal(60_000m, subcontractor.Committed);
    }

    [Fact]
    public void Filters_narrow_the_transactions_by_date_sub_item_party_and_type()
    {
        var all = new[]
        {
            Supply(1, supplierId: 7, quantity: 2m, unitPrice: 1_400m, channel: "Credit", paid: 0m, item: Cement, day: 5),
            Supply(2, supplierId: 8, quantity: 1m, unitPrice: 100m, channel: "Credit", paid: 0m, item: Concrete, day: 20),
        }.Concat(ItemCostBreakdown.FromLabor(new Dictionary<int, IReadOnlyList<LaborItemLine>> { [Concrete] = [LaborLine(1, 500m)] })).ToList();

        Assert.Equal([1], ItemCostBreakdown.Filter(all, BreakdownFilter.None with { SubItemId = Cement }).Select(row => row.RecordId));
        Assert.Equal([2], ItemCostBreakdown.Filter(all, BreakdownFilter.None with { SupplierId = 8 }).Select(row => row.RecordId));
        Assert.Single(ItemCostBreakdown.Filter(all, BreakdownFilter.None with { Type = BreakdownTypes.Labor }));
        Assert.Equal([1], ItemCostBreakdown.Filter(all, BreakdownFilter.None with { To = new DateOnly(2026, 1, 9) }).Select(row => row.RecordId));
        Assert.Empty(ItemCostBreakdown.Filter(all, BreakdownFilter.None with { WorkerId = 99 }));
    }

    [Fact]
    public void The_remaining_budget_is_budgeted_minus_actual_and_has_no_meaning_without_a_budget()
    {
        var within = ItemCostBreakdown.Budget(4, 2, hasApprovedBudget: true, budgeted: 100_000m, actual: 77_800m);
        Assert.Equal(22_200m, within.Remaining);
        Assert.Equal(77.8m, within.PercentUsed);
        Assert.False(within.OverBudget);

        var over = ItemCostBreakdown.Budget(4, 2, true, 50_000m, 77_800m);
        Assert.Equal(-27_800m, over.Remaining);
        Assert.True(over.OverBudget);

        var none = ItemCostBreakdown.Budget(null, null, false, 0m, 77_800m);
        Assert.Null(none.Remaining);
        Assert.Null(none.PercentUsed);
    }

    private static LaborItemLine LaborLine(int workerId, decimal dailyRate) => new(
        LineId: 100 + workerId, SheetId: 1, WorkDate: new DateOnly(2026, 1, 10), Shift: "Day", WorkerId: workerId,
        WorkerNameAr: $"عامل {workerId}", WorkerNameEn: $"Worker {workerId}", TradeId: 1, TradeNameAr: "نجار", TradeNameEn: "Carpenter",
        WorkType: "Concrete", Days: 1m, OvertimeHours: 0m, DailyRate: dailyRate, BaseAmount: dailyRate, OvertimeAmount: 0m,
        AllowancesAmount: 0m, DeductionsAmount: 0m, DeductionReasons: null, LineNet: dailyRate, Portion: 1m, Amount: dailyRate,
        Paid: 0m, Outstanding: dailyRate, PaymentState: PaymentStates.Unpaid, RecordedByUserId: 5);

    private static BreakdownTransaction Supply(
        int id, int supplierId, decimal quantity, decimal unitPrice, string channel, decimal paid, int item = Cement, int day = 10)
    {
        var amount = Money.Round(quantity * unitPrice);
        return new BreakdownTransaction(
            BreakdownTypes.Supply, id, null, null, new DateOnly(2026, 1, day), item, BreakdownParties.Supplier, supplierId, null, null, "Cement",
            quantity, "Ton", unitPrice, amount, paid, amount - paid, PaymentStates.Of(amount, paid), channel, null, null);
    }
}
