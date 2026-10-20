using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Items;

namespace AlMotaheda.Modules.Projects.Tests.Unit.CostManagement;

/// <summary>Main items and sub-items: the screen order and the roll-up, which must never count a record twice.</summary>
public sealed class ItemTreeTests
{
    private const int Concrete = 1;
    private const int Cement = 2;
    private const int Steel = 3;
    private const int Masonry = 4;
    private const int MasonryCement = 5;

    private static readonly ItemHeader[] Items =
    [
        Header(Masonry, "Masonry works", null),
        Header(Steel, "Steel reinforcement", Concrete),
        Header(MasonryCement, "Cement", Masonry),
        Header(Concrete, "Concrete works", null),
        Header(Cement, "Cement", Concrete),
    ];

    [Fact]
    public void Each_main_item_is_followed_by_its_sub_items_by_name()
    {
        var tree = ItemTree.Build(Items);

        Assert.Equal([Concrete, Cement, Steel, Masonry, MasonryCement], tree.Ordered.Select(item => item.Id));
        Assert.Equal([Cement, Steel], tree.SubItemIds(Concrete));
        Assert.Empty(tree.SubItemIds(Cement));
        Assert.Equal([Concrete, Cement, Steel], tree.SelfAndSubItems(Concrete));
    }

    [Fact]
    public void A_main_item_rolls_up_its_own_records_and_its_sub_items_but_the_project_totals_count_each_record_once()
    {
        // Cement 2 t × 1,400 on the sub-item (credit, 1,000 paid), steel 50,000 (custody), labor 5,000 on the main item.
        var supplier = new[]
        {
            new SupplierLedgerRow(11, Cement, SupplierId: 7, Amount: 2_800m, Paid: 1_000m),
            new SupplierLedgerRow(12, Steel, SupplierId: 8, Amount: 50_000m, Paid: 50_000m),
            new SupplierLedgerRow(13, MasonryCement, SupplierId: 7, Amount: 1_100m, Paid: 0m),
        };
        var labor = new[] { new ItemCostTotal(Concrete, 5_000m, 0m, 10, 10) };
        var subcontract = new[] { new ItemCostTotal(Steel, 20_000m, 5_000m, 1, 1) };

        var ledger = ItemsLedger.Compute(supplier, subcontract, labor);
        var tree = ItemTree.Build(Items);

        var concrete = tree.WithSubItems(Items.Single(item => item.Id == Concrete), ledger)!;
        Assert.Equal(5_000m, ledger.Of(Concrete).TotalCost);
        Assert.Equal(77_800m, concrete.TotalCost);
        Assert.Equal(52_800m, concrete.SupplierCost);
        Assert.Equal(20_000m, concrete.SubcontractorCost);
        Assert.Equal(5_000m, concrete.LaborCost);
        Assert.Equal(56_000m, concrete.Paid);
        Assert.Equal(21_800m, concrete.Outstanding);

        // A sub-item has no roll-up, and the project total is the sum of the own figures: 78,900, not 78,900 + 77,800.
        Assert.Null(tree.WithSubItems(Items.Single(item => item.Id == Cement), ledger));
        Assert.Equal(78_900m, ledger.Totals.TotalCost);
        var masonry = tree.WithSubItems(Items.Single(item => item.Id == Masonry), ledger)!;
        Assert.Equal(ledger.Totals.TotalCost, concrete.TotalCost + masonry.TotalCost);
    }

    [Fact]
    public void Machinery_on_a_sub_item_costs_it_once_rolls_up_to_its_main_item_and_a_site_cost_stays_out_of_the_items()
    {
        // Cement 2,800 on the sub-item; machinery: a crane rental 52,500 on the steel sub-item (credit, unpaid), fuel
        // 2,783.13 on the main item (custody, paid), and a site repair 1,500 on no item (custody, paid).
        var supplier = new[] { new SupplierLedgerRow(11, Cement, SupplierId: 7, Amount: 2_800m, Paid: 1_000m) };
        var equipment = new[]
        {
            new ItemCostTotal(Steel, 52_500m, 0m, 1, 1),
            new ItemCostTotal(Concrete, 2_783.13m, 2_783.13m, 1, 1),
            new ItemCostTotal(null, 1_500m, 1_500m, 1, 1),
        };

        var ledger = ItemsLedger.Compute(supplier, [], [], equipment);
        var tree = ItemTree.Build(Items);

        var steel = ledger.Of(Steel);
        Assert.Equal(52_500m, steel.EquipmentCost);
        Assert.Equal(52_500m, steel.TotalCost);
        Assert.Equal(0m, steel.Paid);
        Assert.Equal(52_500m, steel.Outstanding);
        Assert.Equal(1, steel.EquipmentRecordCount);
        Assert.Equal(1, steel.LinkedRecordCount);

        var concrete = tree.WithSubItems(Items.Single(item => item.Id == Concrete), ledger)!;
        Assert.Equal(2_783.13m, ledger.Of(Concrete).TotalCost);
        Assert.Equal(55_283.13m, concrete.EquipmentCost);
        Assert.Equal(58_083.13m, concrete.TotalCost);
        Assert.Equal(3_783.13m, concrete.Paid);
        Assert.Equal(2, concrete.EquipmentCount);

        // Each cost once: the items hold the linked ones, the site repair is neither an item cost nor "not linked yet".
        Assert.Equal(55_283.13m, ledger.Totals.EquipmentCost);
        Assert.Equal(58_083.13m, ledger.Totals.TotalCost);
        Assert.Equal(0m, ledger.Unallocated.TotalCost);
        Assert.Equal(1_500m, ledger.SiteEquipmentCost);
    }

    [Fact]
    public void The_reconciliation_counts_an_equipment_cost_once_whether_or_not_it_is_on_a_work_item()
    {
        // A lab test paid on site with no supplier and no item: an other cost of the project.
        ExpenseSettlementRow[] expenses = [new(1, ExpenseKind.Expense, ExpenseStatus.Approved, PaymentChannel.Custody, ExpenseCategory.Lab, null, null, 700m, 0m)];
        ItemCostTotal[] onTheSite = [new(null, 52_500m, 0m, 1, 1), new(null, 1_500m, 1_500m, 1, 1)];
        ItemCostTotal[] rentalOnSteel = [new(Steel, 52_500m, 0m, 1, 1), new(null, 1_500m, 1_500m, 1, 1)];

        var before = ListProjectItems.Reconcile(Data(expenses, onTheSite));
        var after = ListProjectItems.Reconcile(Data(expenses, rentalOnSteel));

        Assert.Equal(new ItemsReconciliation(0m, 0m, 54_700m, 54_700m), before);
        Assert.Equal(new ItemsReconciliation(52_500m, 0m, 2_200m, 54_700m), after);
    }

    private static ItemsLedgerData Data(IReadOnlyList<ExpenseSettlementRow> expenses, IReadOnlyList<ItemCostTotal> equipment) =>
        new(Items, expenses, ItemsLedger.Compute(SupplierLedgerRow.From(expenses), [], [], equipment));

    private static ItemHeader Header(int id, string nameEn, int? parent) => new(
        id, $"بند {nameEn}", nameEn, WorkTypes.Concrete, MeasurementUnits.All[0], null, null, ProjectItemStatus.Active, null, null,
        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1, null, null, [1], parent);
}
