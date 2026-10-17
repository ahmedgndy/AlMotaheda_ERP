using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Features.PriceVariances;

namespace AlMotaheda.Modules.Projects.Tests.Unit.CostFlow;

/// <summary>
/// The pure rules of docs/cost-flow: the price-variance check, the reference price (project price, then master price),
/// the component breakdown of an item, and the items ledger with equipment and staff costs.
/// </summary>
public sealed class CostFlowRulesTests
{
    [Theory]
    [InlineData(1, 1, 0, 0.01)]        // exact match
    [InlineData(1, 1.01, 0, 0.01)]     // within the rounding allowance
    [InlineData(1, 0.99, 0, 0.01)]
    [InlineData(1, 1.08, 10, 0.01)]    // within a 10 % tolerance
    [InlineData(100, 104, 5, 0.01)]
    public void A_price_within_the_allowance_needs_no_review(decimal reference, decimal actual, decimal tolerancePercent, decimal rounding) =>
        Assert.Null(PriceVarianceRules.Check(reference, actual, 100m, tolerancePercent, rounding));

    [Fact]
    public void A_price_above_the_reference_is_a_variance_with_its_unit_total_and_percent()
    {
        var variance = PriceVarianceRules.Check(1m, 2m, 100m, 0m, 0.01m)!;
        Assert.Equal((1m, 100m, 100m, VarianceDirection.Above), (variance.UnitVariance, variance.TotalVariance, variance.VariancePercent, variance.Direction));
    }

    [Fact]
    public void A_price_below_the_reference_is_a_variance_too()
    {
        var variance = PriceVarianceRules.Check(1m, 0.5m, 100m, 0m, 0.01m)!;
        Assert.Equal((-0.5m, -50m, -50m, VarianceDirection.Below), (variance.UnitVariance, variance.TotalVariance, variance.VariancePercent, variance.Direction));
    }

    [Theory]
    [InlineData(1, 1.11, 10)]   // just above 10 %
    [InlineData(1, 1.02, 0)]    // two piastres with only one allowed
    public void A_price_just_outside_the_allowance_is_flagged(decimal reference, decimal actual, decimal tolerancePercent) =>
        Assert.NotNull(PriceVarianceRules.Check(reference, actual, 1m, tolerancePercent, 0.01m));

    [Fact]
    public void The_allowance_is_the_larger_of_the_percent_and_the_rounding() =>
        Assert.Equal((0.01m, 5m), (PriceVarianceRules.Allowance(1m, 0m, 0.01m), PriceVarianceRules.Allowance(100m, 5m, 0.01m)));

    private static PriceRow Price(int id, decimal price, string from) =>
        new(id, 1, price, null, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), ItemPriceStatus.Approved, null,
            new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), 7, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc), 8, null);

    [Fact]
    public void The_projects_own_price_wins_and_the_master_price_applies_only_when_it_has_none()
    {
        var today = new DateOnly(2026, 6, 1);
        var master = new[] { Price(10, 20m, "2026-01-01"), Price(11, 22m, "2026-05-01") };

        var fromMaster = CatalogPricing.Reference([], master, today);
        Assert.Equal((22m, ReferencePriceSources.Master, 20m), (fromMaster.Current!.Price, fromMaster.Source, fromMaster.Previous!.Price));

        var own = CatalogPricing.Reference([Price(1, 19m, "2026-02-01")], master, today);
        Assert.Equal((19m, ReferencePriceSources.Project), (own.Current!.Price, own.Source));

        // A project price that starts later doesn't apply yet: the master's does.
        var future = CatalogPricing.Reference([Price(1, 19m, "2026-09-01")], master, today);
        Assert.Equal((22m, ReferencePriceSources.Master), (future.Current!.Price, future.Source));

        var none = CatalogPricing.Reference([], [], today);
        Assert.Null(none.Current);
        Assert.Null(none.Source);
    }

    private static ComponentPlan Material(int id, int catalogItemId, string name, string unit) =>
        new(id, ComponentKind.Material, catalogItemId, name, name, unit, null, null, null);

    [Fact]
    public void Components_and_other_add_up_to_the_items_own_cost()
    {
        var plans = new[]
        {
            Material(1, 101, "Cement", "Bag"),
            Material(2, 102, "Sand", "M3"),
            new ComponentPlan(3, ComponentKind.Labor, null, null, null, null, null, null, null),
            new ComponentPlan(4, ComponentKind.Equipment, null, null, null, null, null, null, null),
        };
        var supplies = new[]
        {
            new ComponentSupplyRow(101, "Bag", 100m, 2_000m, 2_000m),
            new ComponentSupplyRow(101, "Ton", 1m, 500m, 0m),     // cost of cement, but not a quantity in bags
            new ComponentSupplyRow(102, "M3", 10m, 1_500m, 1_500m),
            new ComponentSupplyRow(null, "Lot", 1m, 300m, 300m),  // no catalog item: other
            new ComponentSupplyRow(103, "M3", 10m, 2_000m, 0m),   // aggregate, not a component: other
        };

        var result = ComponentBreakdown.Compute(plans, supplies, new SourceTotal(800m, 400m, 2), new SourceTotal(500m, 500m, 1), new SourceTotal(3_000m, 0m, 1));

        var cement = result.Components.Single(component => component.CatalogItemId == 101);
        Assert.Equal((2_500m, 100m, 2_000m), (cement.Cost, cement.ActualQuantity!.Value, cement.Paid));
        Assert.Equal(1_500m, result.Components.Single(component => component.CatalogItemId == 102).Cost);
        Assert.Equal((800m, 500m), (result.Components.Single(component => component.Kind == "Labor").Cost, result.Components.Single(component => component.Kind == "Equipment").Cost));

        // Subcontract work has no component here, so it is "other" with the unmatched supplies.
        Assert.Equal(2_300m + 3_000m, result.Other.Cost);
        // Supplies 6,300 + labor 800 + equipment 500 + subcontract 3,000.
        Assert.Equal(10_600m, result.Total.Cost);
        Assert.Equal(result.Total.Cost, result.Components.Sum(component => component.Cost) + result.Other.Cost);
        Assert.Equal(result.Total.Paid, result.Components.Sum(component => component.Paid) + result.Other.Paid);
    }

    [Fact]
    public void Equipment_and_staff_costs_join_the_items_ledger_and_its_not_linked_group()
    {
        var supplies = new[] { new SupplierLedgerRow(1, 7, 3, 1_000m, 1_000m) };
        var others = new[]
        {
            new GroupItemTotal(CostGroups.Equipment, new ItemCostTotal(7, 1_500m, 1_500m, 1, 1)),
            new GroupItemTotal(CostGroups.Equipment, new ItemCostTotal(null, 2_000m, 0m, 1, 1)),
            new GroupItemTotal(CostGroups.Staff, new ItemCostTotal(7, 5_000m, 0m, 1, 1)),
        };

        var result = ItemsLedger.Compute(supplies, [], [], others);

        var item = result.Of(7);
        Assert.Equal((1_500m, 5_000m, 7_500m, 2_500m), (item.EquipmentCost, item.StaffCost, item.TotalCost, item.Paid));
        Assert.Equal(2_000m, result.Unallocated.EquipmentCost);
        Assert.Equal(7_500m, result.Totals.TotalCost);
        Assert.Throws<ArgumentOutOfRangeException>(() => ItemsLedger.Compute([], [], [], [new GroupItemTotal(CostGroups.Labor, new ItemCostTotal(7, 1m, 0m, 1, 1))]));
    }
}
