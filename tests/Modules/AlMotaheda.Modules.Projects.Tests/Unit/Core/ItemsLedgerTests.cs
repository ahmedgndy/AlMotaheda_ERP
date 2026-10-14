using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Items;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Core;

/// <summary>
/// The project items arithmetic, to the pound, with no database. The same class feeds the table, the item
/// detail, the not-linked detail and the overview, so these numbers are the numbers every screen shows.
/// </summary>
public sealed class ItemsLedgerTests
{
    private const int ItemA = 1;
    private const int ItemB = 2;
    private const int ItemC = 3;

    /// <summary>
    /// The nine-row example: eight approved supplier records on three items and one not linked yet, plus the
    /// subcontract and labor totals the other modules report.
    /// </summary>
    private static readonly SupplierLedgerRow[] NineRows =
    [
        new(101, ItemA, SupplierId: 10, Amount: 2_500_000m, Paid: 2_500_000m),
        new(102, ItemA, SupplierId: 10, Amount: 1_500_000m, Paid: 1_000_000m),
        new(103, ItemA, SupplierId: 11, Amount: 700_000m, Paid: 700_000m),
        new(104, ItemA, SupplierId: 11, Amount: 500_000m, Paid: 500_000m),
        new(105, ItemB, SupplierId: 10, Amount: 1_250_000m, Paid: 1_250_000m),
        new(106, ItemB, SupplierId: 10, Amount: 750_000m, Paid: 750_000m),
        new(107, ItemB, SupplierId: 12, Amount: 347_350m, Paid: 0m),
        new(108, ItemC, SupplierId: 10, Amount: 600_000m, Paid: 600_000m),
        new(109, null, SupplierId: 12, Amount: 114_700m, Paid: 111_550m),
    ];

    private static readonly ItemCostTotal[] Subcontract =
    [
        new(ItemA, Cost: 500_000m, Paid: 300_000m, RecordCount: 2, PartyCount: 1),
        new(ItemC, Cost: 500_000m, Paid: 0m, RecordCount: 1, PartyCount: 1),
    ];

    private static readonly ItemCostTotal[] Labor =
    [
        new(ItemA, Cost: 400_000m, Paid: 10_000m, RecordCount: 30, PartyCount: 12),
        new(ItemB, Cost: 269_819m, Paid: 3_252m, RecordCount: 20, PartyCount: 8),
        new(null, Cost: 53_100m, Paid: 53_100m, RecordCount: 4, PartyCount: 3),
    ];

    [Fact]
    public void The_nine_row_example_adds_up_to_the_pound()
    {
        var result = ItemsLedger.Compute(NineRows, Subcontract, Labor);

        var totals = result.Totals;
        Assert.Equal(8_147_350m, totals.SupplierCost);
        Assert.Equal(1_000_000m, totals.SubcontractorCost);
        Assert.Equal(9_147_350m, totals.SupplierAndSubcontractorCost);
        Assert.Equal(669_819m, totals.LaborCost);
        Assert.Equal(9_817_169m, totals.TotalCost);
        Assert.Equal(7_300_000m, totals.SupplierPaid);
        Assert.Equal(300_000m, totals.SubcontractorPaid);
        Assert.Equal(13_252m, totals.LaborPaid);
        Assert.Equal(7_613_252m, totals.Paid);
        Assert.Equal(2_203_917m, totals.Outstanding);
        Assert.Equal(8, totals.SupplierRecordCount);
        Assert.Equal(3, totals.SupplierCount);
        Assert.Equal(3, totals.SubcontractRecordCount);
        Assert.Equal(50, totals.LaborRecordCount);
        Assert.Equal(11, totals.LinkedRecordCount);

        var unallocated = result.Unallocated;
        Assert.Equal(114_700m, unallocated.SupplierCost);
        Assert.Equal(53_100m, unallocated.LaborCost);
        Assert.Equal(0m, unallocated.SubcontractorCost);
        Assert.Equal(167_800m, unallocated.TotalCost);
        Assert.Equal(164_650m, unallocated.Paid);
        Assert.Equal(3_150m, unallocated.Outstanding);
        Assert.Equal(1, unallocated.SupplierRecordCount);
        Assert.Equal(4, unallocated.LaborRecordCount);
    }

    [Fact]
    public void Each_item_row_has_its_own_figures()
    {
        var result = ItemsLedger.Compute(NineRows, Subcontract, Labor);

        var a = result.Of(ItemA);
        Assert.Equal(5_200_000m, a.SupplierCost);
        Assert.Equal(4_700_000m, a.SupplierPaid);
        Assert.Equal(500_000m, a.SubcontractorCost);
        Assert.Equal(400_000m, a.LaborCost);
        Assert.Equal(6_100_000m, a.TotalCost);
        Assert.Equal(5_010_000m, a.Paid);
        Assert.Equal(1_090_000m, a.Outstanding);
        Assert.Equal(4, a.SupplierRecordCount);
        Assert.Equal(2, a.SupplierCount);
        Assert.Equal(2, a.SubcontractRecordCount);
        Assert.Equal(1, a.SubcontractorCount);
        Assert.Equal(30, a.LaborRecordCount);
        Assert.Equal(12, a.WorkerCount);
        Assert.Equal(6, a.LinkedRecordCount);

        var b = result.Of(ItemB);
        Assert.Equal(2_347_350m, b.SupplierCost);
        Assert.Equal(0m, b.SubcontractorCost);
        Assert.Equal(269_819m, b.LaborCost);
        Assert.Equal(2_617_169m, b.TotalCost);
        Assert.Equal(2_003_252m, b.Paid);
        Assert.Equal(613_917m, b.Outstanding);
        Assert.Equal(2, b.SupplierCount);

        var c = result.Of(ItemC);
        Assert.Equal(600_000m, c.SupplierCost);
        Assert.Equal(500_000m, c.SubcontractorCost);
        Assert.Equal(0m, c.LaborCost);
        Assert.Equal(1_100_000m, c.TotalCost);
        Assert.Equal(600_000m, c.Paid);
        Assert.Equal(500_000m, c.Outstanding);

        Assert.Equal(result.Totals.TotalCost, a.TotalCost + b.TotalCost + c.TotalCost);
        Assert.Equal(result.Totals.Paid, a.Paid + b.Paid + c.Paid);
    }

    [Fact]
    public void The_not_linked_group_is_never_inside_the_totals()
    {
        var result = ItemsLedger.Compute(NineRows, Subcontract, Labor);

        Assert.Equal(9_817_169m, result.Totals.TotalCost);
        Assert.Equal(167_800m, result.Unallocated.TotalCost);
        Assert.Equal(9_984_969m, result.Totals.TotalCost + result.Unallocated.TotalCost);
        Assert.DoesNotContain(result.ByItem.Keys, key => key == 0);
    }

    [Fact]
    public void Linking_a_record_moves_its_amounts_into_the_item_and_out_of_the_group_without_changing_the_project_total()
    {
        var before = ItemsLedger.Compute(NineRows, Subcontract, Labor);

        // The person links record 109 (114,700, of which 111,550 is paid) to item C. Same rows, one link changed.
        var after = ItemsLedger.Compute(
            NineRows.Select(row => row.ExpenseId == 109 ? row with { ItemId = ItemC } : row).ToList(),
            Subcontract,
            Labor);

        Assert.Equal(before.Of(ItemC).SupplierCost + 114_700m, after.Of(ItemC).SupplierCost);
        Assert.Equal(before.Of(ItemC).Paid + 111_550m, after.Of(ItemC).Paid);
        Assert.Equal(before.Of(ItemC).Outstanding + 3_150m, after.Of(ItemC).Outstanding);
        Assert.Equal(before.Of(ItemC).SupplierRecordCount + 1, after.Of(ItemC).SupplierRecordCount);
        Assert.Equal(before.Of(ItemC).SupplierCount + 1, after.Of(ItemC).SupplierCount);

        Assert.Equal(0m, after.Unallocated.SupplierCost);
        Assert.Equal(0, after.Unallocated.SupplierRecordCount);
        Assert.Equal(53_100m, after.Unallocated.TotalCost);

        Assert.Equal(before.Totals.TotalCost + 114_700m, after.Totals.TotalCost);
        Assert.Equal(
            before.Totals.TotalCost + before.Unallocated.TotalCost,
            after.Totals.TotalCost + after.Unallocated.TotalCost);
        Assert.Equal(
            before.Totals.RecordCount + before.Unallocated.RecordCount,
            after.Totals.RecordCount + after.Unallocated.RecordCount);
    }

    [Fact]
    public void Recorded_returned_and_rejected_records_are_in_no_total()
    {
        ExpenseSettlementRow Row(int id, ExpenseStatus status, decimal amount) =>
            new(id, ExpenseKind.Supply, status, PaymentChannel.Credit, null, SupplierId: 5, ProjectItemId: ItemA, amount, Allocated: 0);

        var rows = SupplierLedgerRow.From(
        [
            Row(1, ExpenseStatus.Approved, 1_000m),
            Row(2, ExpenseStatus.Recorded, 2_000m),
            Row(3, ExpenseStatus.Returned, 4_000m),
            Row(4, ExpenseStatus.Rejected, 8_000m),
        ]);

        var result = ItemsLedger.Compute(rows, [], []);

        Assert.Single(rows);
        Assert.Equal(1_000m, result.Totals.TotalCost);
        Assert.Equal(1, result.Totals.SupplierRecordCount);
        Assert.Equal(0m, result.Unallocated.TotalCost);
    }

    [Fact]
    public void A_site_expense_with_no_supplier_and_no_item_is_an_other_cost_not_a_ledger_row()
    {
        ExpenseSettlementRow Row(int id, ExpenseKind kind, int? supplierId, int? itemId) =>
            new(id, kind, ExpenseStatus.Approved, PaymentChannel.Custody, ExpenseCategory.Lab, supplierId, itemId, 100m, 0);

        var rows = SupplierLedgerRow.From(
        [
            Row(1, ExpenseKind.Expense, null, null),   // other project cost
            Row(2, ExpenseKind.Expense, 7, null),      // from a supplier: not linked yet
            Row(3, ExpenseKind.Expense, null, ItemA),  // linked by hand
            Row(4, ExpenseKind.Supply, 7, null),       // a supply is always supplier-side
        ]);

        var result = ItemsLedger.Compute(rows, [], []);

        Assert.Equal([2, 3, 4], rows.Select(row => row.ExpenseId));
        Assert.Equal(100m, result.Totals.TotalCost);
        Assert.Equal(200m, result.Unallocated.TotalCost);
    }

    [Fact]
    public void Custody_is_paid_at_approval_and_credit_only_through_allocations()
    {
        Assert.Equal(500m, ExpenseSettlement.Paid(ExpenseStatus.Approved, PaymentChannel.Custody, 500m, allocated: 0));
        Assert.Equal(0m, ExpenseSettlement.Paid(ExpenseStatus.Approved, PaymentChannel.Credit, 500m, allocated: 0));
        Assert.Equal(200m, ExpenseSettlement.Paid(ExpenseStatus.Approved, PaymentChannel.Credit, 500m, allocated: 200m));
        Assert.Equal(0m, ExpenseSettlement.Paid(ExpenseStatus.Recorded, PaymentChannel.Custody, 500m, allocated: 0));
        Assert.Equal(0m, ExpenseSettlement.Recognized(ExpenseStatus.Returned, 500m));
        Assert.Equal(500m, ExpenseSettlement.Recognized(ExpenseStatus.Approved, 500m));

        var credit = new ExpenseSettlementRow(1, ExpenseKind.Supply, ExpenseStatus.Approved, PaymentChannel.Credit, null, 5, null, 500m, 200m);
        Assert.Equal(300m, ExpenseSettlement.Outstanding(credit));
        Assert.Equal(PaymentStates.PartiallyPaid, ExpenseSettlement.PaymentState(credit));
        Assert.Equal(PaymentStates.Paid, ExpenseSettlement.PaymentState(credit with { Allocated = 500m }));
        Assert.Equal(PaymentStates.Unpaid, ExpenseSettlement.PaymentState(credit with { Allocated = 0m }));
        Assert.Null(ExpenseSettlement.PaymentState(credit with { Status = ExpenseStatus.Recorded }));
    }

    [Fact]
    public void The_summary_counts_pending_and_returned_apart_from_recognized_cost()
    {
        ExpenseSettlementRow Row(int id, ExpenseKind kind, ExpenseStatus status, PaymentChannel channel, decimal amount, decimal allocated) =>
            new(id, kind, status, channel, null, 5, null, amount, allocated);

        var summary = ExpenseSummary.Compute(
        [
            Row(1, ExpenseKind.Supply, ExpenseStatus.Approved, PaymentChannel.Credit, 10_000m, 4_000m),
            Row(2, ExpenseKind.Supply, ExpenseStatus.Approved, PaymentChannel.Custody, 2_500m, 0m),
            Row(3, ExpenseKind.Expense, ExpenseStatus.Approved, PaymentChannel.Custody, 700m, 0m),
            Row(4, ExpenseKind.Expense, ExpenseStatus.Recorded, PaymentChannel.Custody, 300m, 0m),
            Row(5, ExpenseKind.Supply, ExpenseStatus.Recorded, PaymentChannel.Credit, 1_200m, 0m),
            Row(6, ExpenseKind.Supply, ExpenseStatus.Returned, PaymentChannel.Credit, 9_000m, 0m),
            Row(7, ExpenseKind.Supply, ExpenseStatus.Rejected, PaymentChannel.Credit, 99_000m, 0m),
        ]);

        Assert.Equal(12_500m, summary.SuppliesRecognized);
        Assert.Equal(700m, summary.ExpensesRecognized);
        Assert.Equal(13_200m, summary.RecognizedTotal);
        Assert.Equal(7_200m, summary.Paid);
        Assert.Equal(6_000m, summary.Outstanding);
        Assert.Equal(2, summary.PendingReviewCount);
        Assert.Equal(1_500m, summary.PendingReviewAmount);
        Assert.Equal(1, summary.ReturnedCount);
    }

    [Fact]
    public void Supplier_balances_come_from_approved_records_only_and_count_open_invoices()
    {
        ExpenseSettlementRow Row(int id, int supplierId, ExpenseStatus status, PaymentChannel channel, decimal amount, decimal allocated) =>
            new(id, ExpenseKind.Supply, status, channel, null, supplierId, null, amount, allocated);

        var balances = Features.SupplierPayments.SupplierBalances.Compute(
        [
            Row(1, 10, ExpenseStatus.Approved, PaymentChannel.Credit, 1_000m, 1_000m),
            Row(2, 10, ExpenseStatus.Approved, PaymentChannel.Credit, 2_000m, 500m),
            Row(3, 10, ExpenseStatus.Approved, PaymentChannel.Custody, 300m, 0m),
            Row(4, 10, ExpenseStatus.Recorded, PaymentChannel.Credit, 5_000m, 0m),
            Row(5, 11, ExpenseStatus.Approved, PaymentChannel.Credit, 800m, 0m),
            Row(6, 12, ExpenseStatus.Rejected, PaymentChannel.Credit, 800m, 0m),
        ]);

        Assert.Equal(2, balances.Count);

        var first = balances[0];
        Assert.Equal(10, first.SupplierId);
        Assert.Equal(3_000m, first.CreditRecognized);
        Assert.Equal(300m, first.CustodyRecognized);
        Assert.Equal(1_800m, first.Paid);
        Assert.Equal(1_500m, first.Outstanding);
        Assert.Equal(1, first.OpenInvoices);

        var second = balances[1];
        Assert.Equal(11, second.SupplierId);
        Assert.Equal(800m, second.Outstanding);
        Assert.Equal(1, second.OpenInvoices);
    }
}
