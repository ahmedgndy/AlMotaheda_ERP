using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Features.Items;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Core;

/// <summary>
/// The items table ("بنود") of the company's own example, row by row and to the pound. The figures are built
/// from source records the way the three modules report them, then run through <see cref="ItemsLedger"/>, the one
/// class the table, the item detail and the "not linked yet" block all use.
/// </summary>
public sealed class SampleItemsTableTests
{
    private const int Concrete = 1;      // الأعمال الخرسانية
    private const int Rebar = 2;         // حديد التسليح
    private const int Cement = 3;        // الأسمنت
    private const int Masonry = 4;       // المباني والطوب
    private const int Formwork = 5;      // النجارة المسلحة (الشدات)
    private const int Tiles = 6;         // البلاط والسيراميك
    private const int Electrical = 7;    // الأعمال الكهربائية
    private const int Plumbing = 8;      // أعمال السباكة والصرف
    private const int General = 9;       // أعمال عامة بالموقع

    // Approved supplier records (this module). Paid is what ExpenseSettlement worked out.
    private static readonly SupplierLedgerRow[] Suppliers =
    [
        new(1, Concrete, SupplierId: 10, Amount: 2_852_950m, Paid: 2_852_950m),
        new(2, Rebar, SupplierId: 11, Amount: 2_000_000m, Paid: 1_871_809m),
        new(3, Rebar, SupplierId: 12, Amount: 1_698_709m, Paid: 0m),
        new(4, Cement, SupplierId: 13, Amount: 2_124_645m, Paid: 1_804_845m),
        new(5, Masonry, SupplierId: 14, Amount: 200_246m, Paid: 200_246m),
        new(6, Electrical, SupplierId: 15, Amount: 20_000m, Paid: 20_000m),
        new(7, null, SupplierId: 16, Amount: 84_700m, Paid: 84_700m),
    ];

    // Approved subcontract work (Subcontractors module), one total per item.
    private static readonly ItemCostTotal[] Subcontracts =
    [
        new(Masonry, Cost: 160_000m, Paid: 160_000m, RecordCount: 1, PartyCount: 1),
        new(Electrical, Cost: 60_000m, Paid: 40_000m, RecordCount: 2, PartyCount: 1),
        new(Plumbing, Cost: 30_800m, Paid: 30_800m, RecordCount: 1, PartyCount: 1),
        new(null, Cost: 30_000m, Paid: 26_850m, RecordCount: 1, PartyCount: 1),
    ];

    // Approved labor allocated to items (Workforce module), one total per item.
    private static readonly ItemCostTotal[] Labor =
    [
        new(Rebar, Cost: 108_658m, Paid: 108_658m, RecordCount: 40, PartyCount: 6),
        new(Masonry, Cost: 93_282m, Paid: 87_718m, RecordCount: 35, PartyCount: 5),
        new(Formwork, Cost: 94_183m, Paid: 92_343m, RecordCount: 36, PartyCount: 5),
        new(Tiles, Cost: 49_719m, Paid: 48_125m, RecordCount: 18, PartyCount: 3),
        new(Electrical, Cost: 66_820m, Paid: 57_425m, RecordCount: 25, PartyCount: 4),
        new(Plumbing, Cost: 49_640m, Paid: 35_391m, RecordCount: 20, PartyCount: 7),
        new(General, Cost: 207_517m, Paid: 202_942m, RecordCount: 80, PartyCount: 14),
        new(null, Cost: 53_100m, Paid: 53_100m, RecordCount: 1, PartyCount: 1),
    ];

    public static TheoryData<int, int, decimal, decimal, decimal, decimal, decimal> Rows => new()
    {
        // item, linked records, suppliers + subcontractors, internal labor, total, paid, remaining
        { Concrete, 1, 2_852_950m, 0m, 2_852_950m, 2_852_950m, 0m },
        { Rebar, 2, 3_698_709m, 108_658m, 3_807_367m, 1_980_467m, 1_826_900m },
        { Cement, 1, 2_124_645m, 0m, 2_124_645m, 1_804_845m, 319_800m },
        { Masonry, 2, 360_246m, 93_282m, 453_528m, 447_964m, 5_564m },
        { Formwork, 0, 0m, 94_183m, 94_183m, 92_343m, 1_840m },
        { Tiles, 0, 0m, 49_719m, 49_719m, 48_125m, 1_594m },
        { Electrical, 3, 80_000m, 66_820m, 146_820m, 117_425m, 29_395m },
        { Plumbing, 1, 30_800m, 49_640m, 80_440m, 66_191m, 14_249m },
        { General, 0, 0m, 207_517m, 207_517m, 202_942m, 4_575m },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_row_of_the_example_table_matches_to_the_pound(
        int itemId, int linkedRecords, decimal suppliers, decimal labor, decimal total, decimal paid, decimal remaining)
    {
        var row = ItemsLedger.Compute(Suppliers, Subcontracts, Labor).Of(itemId);

        Assert.Equal(linkedRecords, row.LinkedRecordCount);
        Assert.Equal(suppliers, row.SupplierAndSubcontractorCost);
        Assert.Equal(labor, row.LaborCost);
        Assert.Equal(total, row.TotalCost);
        Assert.Equal(row.SupplierAndSubcontractorCost + row.LaborCost, row.TotalCost);
        Assert.Equal(paid, row.Paid);
        Assert.Equal(remaining, row.Outstanding);
    }

    [Fact]
    public void The_footer_adds_up_and_leaves_the_not_linked_records_out()
    {
        var result = ItemsLedger.Compute(Suppliers, Subcontracts, Labor);

        Assert.Equal(9_147_350m, result.Totals.SupplierAndSubcontractorCost);
        Assert.Equal(669_819m, result.Totals.LaborCost);
        Assert.Equal(9_817_169m, result.Totals.TotalCost);
        Assert.Equal(7_613_252m, result.Totals.Paid);
        Assert.Equal(2_203_917m, result.Totals.Outstanding);

        // The footer is exactly the sum of the nine rows.
        var rows = Enumerable.Range(Concrete, 9).Select(result.Of).ToList();
        Assert.Equal(result.Totals.TotalCost, rows.Sum(row => row.TotalCost));
        Assert.Equal(result.Totals.Paid, rows.Sum(row => row.Paid));
    }

    [Fact]
    public void The_not_linked_block_matches_the_example()
    {
        var unallocated = ItemsLedger.Compute(Suppliers, Subcontracts, Labor).Unallocated;

        Assert.Equal(3, unallocated.RecordCount);
        Assert.Equal(114_700m, unallocated.SupplierAndSubcontractorCost);
        Assert.Equal(53_100m, unallocated.LaborCost);
        Assert.Equal(167_800m, unallocated.TotalCost);
        Assert.Equal(164_650m, unallocated.Paid);
        Assert.Equal(3_150m, unallocated.Outstanding);
    }

    [Fact]
    public void Linking_a_not_linked_record_moves_it_without_counting_it_twice()
    {
        var before = ItemsLedger.Compute(Suppliers, Subcontracts, Labor);

        // The same source record (id 7) now carries the plumbing item. No new record is added.
        var linked = Suppliers.Select(row => row.ExpenseId == 7 ? row with { ItemId = Plumbing } : row).ToArray();
        var after = ItemsLedger.Compute(linked, Subcontracts, Labor);

        Assert.Equal(before.Of(Plumbing).TotalCost + 84_700m, after.Of(Plumbing).TotalCost);
        Assert.Equal(before.Unallocated.TotalCost - 84_700m, after.Unallocated.TotalCost);
        Assert.Equal(
            before.Totals.TotalCost + before.Unallocated.TotalCost,
            after.Totals.TotalCost + after.Unallocated.TotalCost);
        Assert.Equal(2, after.Unallocated.RecordCount);
    }
}
