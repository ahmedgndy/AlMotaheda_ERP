using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Features.Expenses;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>
/// One approved supplier-side record as the items ledger sees it. <see cref="Paid"/> is already settled by
/// <see cref="ExpenseSettlement"/>: the amount on custody, the allocations on credit.
/// </summary>
/// <param name="ItemId">The project item, or <c>null</c> for "not linked to an item yet".</param>
internal sealed record SupplierLedgerRow(int ExpenseId, int? ItemId, int? SupplierId, decimal Amount, decimal Paid)
{
    /// <summary>The ledger rows among the project's records: approved, and on the items screens.</summary>
    public static IReadOnlyList<SupplierLedgerRow> From(IEnumerable<ExpenseSettlementRow> rows) =>
        rows.Where(row => row.Status == Data.ExpenseStatus.Approved && ExpenseSettlement.IsSupplierSide(row))
            .Select(row => new SupplierLedgerRow(row.ExpenseId, row.ProjectItemId, row.SupplierId, row.Amount, ExpenseSettlement.Paid(row)))
            .ToList();
}

/// <summary>
/// The figures of one project item, of the whole project, or of the "not linked yet" group. Every number is a sum
/// of approved source records from sources that never overlap: supplier records (this module), subcontract work
/// (Subcontractors), internal labor (Workforce), equipment costs (Fleet) and allocated staff salaries (HR).
/// </summary>
internal sealed record ItemFigures(
    int SupplierRecordCount,
    int SupplierCount,
    int SubcontractRecordCount,
    int SubcontractorCount,
    int LaborRecordCount,
    int WorkerCount,
    decimal SupplierCost,
    decimal SubcontractorCost,
    decimal LaborCost,
    decimal SupplierPaid,
    decimal SubcontractorPaid,
    decimal LaborPaid,
    int EquipmentRecordCount = 0,
    decimal EquipmentCost = 0,
    decimal EquipmentPaid = 0,
    int StaffRecordCount = 0,
    decimal StaffCost = 0,
    decimal StaffPaid = 0)
{
    public static ItemFigures Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Supplier and subcontract records: the records a person can link to an item one by one.</summary>
    public int LinkedRecordCount => SupplierRecordCount + SubcontractRecordCount;

    public decimal SupplierAndSubcontractorCost => SupplierCost + SubcontractorCost;

    public decimal TotalCost => SupplierAndSubcontractorCost + LaborCost + EquipmentCost + StaffCost;

    public decimal Paid => SupplierPaid + SubcontractorPaid + LaborPaid + EquipmentPaid + StaffPaid;

    public decimal Outstanding => TotalCost - Paid;

    public int RecordCount => SupplierRecordCount + SubcontractRecordCount + LaborRecordCount + EquipmentRecordCount + StaffRecordCount;

    public ItemFigures Add(ItemFigures other) => new(
        SupplierRecordCount + other.SupplierRecordCount,
        SupplierCount + other.SupplierCount,
        SubcontractRecordCount + other.SubcontractRecordCount,
        SubcontractorCount + other.SubcontractorCount,
        LaborRecordCount + other.LaborRecordCount,
        WorkerCount + other.WorkerCount,
        SupplierCost + other.SupplierCost,
        SubcontractorCost + other.SubcontractorCost,
        LaborCost + other.LaborCost,
        SupplierPaid + other.SupplierPaid,
        SubcontractorPaid + other.SubcontractorPaid,
        LaborPaid + other.LaborPaid,
        EquipmentRecordCount + other.EquipmentRecordCount,
        EquipmentCost + other.EquipmentCost,
        EquipmentPaid + other.EquipmentPaid,
        StaffRecordCount + other.StaffRecordCount,
        StaffCost + other.StaffCost,
        StaffPaid + other.StaffPaid);

    /// <summary>The cost of one group (<c>CostGroups</c> name) inside these figures.</summary>
    public decimal CostOf(string group) => group switch
    {
        CostGroups.Equipment => EquipmentCost,
        CostGroups.Staff => StaffCost,
        _ => 0,
    };
}

/// <summary>One per-item total of an <see cref="IItemCostSource"/>, tagged with its group.</summary>
internal sealed record GroupItemTotal(string Group, ItemCostTotal Total);

/// <summary>What <see cref="ItemsLedger"/> computed for one project.</summary>
/// <param name="ByItem">The figures of every item that has at least one record. Items without records are absent.</param>
/// <param name="Totals">The sums over all items of the project, active and inactive. The not-linked group is not in them.</param>
/// <param name="Unallocated">The figures of approved records that have no item yet.</param>
internal sealed record ItemsLedgerResult(IReadOnlyDictionary<int, ItemFigures> ByItem, ItemFigures Totals, ItemFigures Unallocated)
{
    public ItemFigures Of(int itemId) => ByItem.TryGetValue(itemId, out var figures) ? figures : ItemFigures.Empty;
}

/// <summary>
/// The arithmetic of the project items table ("بنود"), with no database access. The table, the item detail, the
/// "not linked yet" detail and the overview all call this one class over the same loaded rows, so a total at the
/// bottom of a detail always equals the row in the table (decision G5).
/// </summary>
internal static class ItemsLedger
{
    /// <param name="supplierRows">Approved supplier-side records of the project, linked or not.</param>
    /// <param name="subcontract">One total per item from Subcontractors, plus one with a <c>null</c> item.</param>
    /// <param name="labor">One total per item from Workforce, plus one with a <c>null</c> item.</param>
    /// <param name="others">Per-item totals of the other item cost sources: equipment (Fleet) and staff (HR).</param>
    public static ItemsLedgerResult Compute(
        IReadOnlyList<SupplierLedgerRow> supplierRows,
        IReadOnlyList<ItemCostTotal> subcontract,
        IReadOnlyList<ItemCostTotal> labor,
        IReadOnlyList<GroupItemTotal>? others = null)
    {
        var byItem = new Dictionary<int, ItemFigures>();
        var unallocated = ItemFigures.Empty;

        void Put(int? itemId, ItemFigures figures)
        {
            if (itemId is null)
            {
                unallocated = unallocated.Add(figures);
            }
            else
            {
                byItem[itemId.Value] = byItem.TryGetValue(itemId.Value, out var current) ? current.Add(figures) : figures;
            }
        }

        foreach (var group in supplierRows.GroupBy(row => row.ItemId))
        {
            Put(group.Key, ItemFigures.Empty with
            {
                SupplierRecordCount = group.Count(),
                SupplierCount = group.Where(row => row.SupplierId is not null).Select(row => row.SupplierId).Distinct().Count(),
                SupplierCost = group.Sum(row => row.Amount),
                SupplierPaid = group.Sum(row => row.Paid),
            });
        }

        // The other modules already group by item; a module that sends two totals for one item is still summed.
        foreach (var total in subcontract)
        {
            Put(total.ItemId, ItemFigures.Empty with
            {
                SubcontractRecordCount = total.RecordCount,
                SubcontractorCount = total.PartyCount,
                SubcontractorCost = total.Cost,
                SubcontractorPaid = total.Paid,
            });
        }

        foreach (var total in labor)
        {
            Put(total.ItemId, ItemFigures.Empty with
            {
                LaborRecordCount = total.RecordCount,
                WorkerCount = total.PartyCount,
                LaborCost = total.Cost,
                LaborPaid = total.Paid,
            });
        }

        foreach (var (group, total) in others ?? [])
        {
            Put(total.ItemId, group switch
            {
                CostGroups.Equipment => ItemFigures.Empty with { EquipmentRecordCount = total.RecordCount, EquipmentCost = total.Cost, EquipmentPaid = total.Paid },
                CostGroups.Staff => ItemFigures.Empty with { StaffRecordCount = total.RecordCount, StaffCost = total.Cost, StaffPaid = total.Paid },
                _ => throw new ArgumentOutOfRangeException(nameof(others), group, "Only equipment and staff are item cost sources."),
            });
        }

        // A supplier on two items is one supplier of the project. Worker and subcontractor counts come per item
        // from the other modules, so at project level they are the sum of the item counts.
        var totals = byItem.Values.Aggregate(ItemFigures.Empty, (sum, figures) => sum.Add(figures)) with
        {
            SupplierCount = supplierRows
                .Where(row => row.ItemId is not null && row.SupplierId is not null)
                .Select(row => row.SupplierId)
                .Distinct()
                .Count(),
        };

        return new ItemsLedgerResult(byItem, totals, unallocated);
    }
}
