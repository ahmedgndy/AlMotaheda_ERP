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
/// of approved source records from five sources that never overlap: supplier records (this module, with a
/// supplier), direct costs (this module, no supplier: cash purchases from custody and site expenses), subcontract
/// work (Subcontractors), internal labor (Workforce) and equipment costs (Fleet, machinery).
/// </summary>
/// <param name="EquipmentCount">How many different machines have approved costs on the item.</param>
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
    int DirectRecordCount = 0,
    decimal DirectCost = 0,
    decimal DirectPaid = 0,
    int EquipmentRecordCount = 0,
    int EquipmentCount = 0,
    decimal EquipmentCost = 0,
    decimal EquipmentPaid = 0)
{
    public static ItemFigures Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>Supplier, direct, subcontract and equipment records: the records a person can link to an item one by one.</summary>
    public int LinkedRecordCount => SupplierRecordCount + SubcontractRecordCount + DirectRecordCount + EquipmentRecordCount;

    public decimal SupplierAndSubcontractorCost => SupplierCost + SubcontractorCost;

    /// <summary>Every source once: supplier, subcontract, labor, direct and equipment cost.</summary>
    public decimal TotalCost => SupplierAndSubcontractorCost + LaborCost + DirectCost + EquipmentCost;

    public decimal Paid => SupplierPaid + SubcontractorPaid + LaborPaid + DirectPaid + EquipmentPaid;

    public decimal Outstanding => TotalCost - Paid;

    public int RecordCount => SupplierRecordCount + SubcontractRecordCount + LaborRecordCount + DirectRecordCount + EquipmentRecordCount;

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
        DirectRecordCount + other.DirectRecordCount,
        DirectCost + other.DirectCost,
        DirectPaid + other.DirectPaid,
        EquipmentRecordCount + other.EquipmentRecordCount,
        EquipmentCount + other.EquipmentCount,
        EquipmentCost + other.EquipmentCost,
        EquipmentPaid + other.EquipmentPaid);
}

/// <summary>What <see cref="ItemsLedger"/> computed for one project.</summary>
/// <param name="ByItem">The figures of every item that has at least one record. Items without records are absent.</param>
/// <param name="Totals">The sums over all items of the project, active and inactive. The not-linked group is not in them.</param>
/// <param name="Unallocated">The figures of approved records that have no item yet.</param>
/// <param name="SiteEquipmentCost">
/// Approved equipment costs on no work item: costs of the whole site. In the project's other costs, never in
/// <paramref name="Totals"/> or <paramref name="Unallocated"/> (linking a machine's cost to an item is optional).
/// </param>
internal sealed record ItemsLedgerResult(
    IReadOnlyDictionary<int, ItemFigures> ByItem, ItemFigures Totals, ItemFigures Unallocated, decimal SiteEquipmentCost = 0)
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
    /// <param name="equipment">
    /// One total per item from Fleet, plus one with a <c>null</c> item for the costs of the whole site, which go to
    /// <see cref="ItemsLedgerResult.SiteEquipmentCost"/>, not to the not-linked group. <c>null</c> = no equipment.
    /// </param>
    public static ItemsLedgerResult Compute(
        IReadOnlyList<SupplierLedgerRow> supplierRows,
        IReadOnlyList<ItemCostTotal> subcontract,
        IReadOnlyList<ItemCostTotal> labor,
        IReadOnlyList<ItemCostTotal>? equipment = null)
    {
        var byItem = new Dictionary<int, ItemFigures>();
        var unallocated = ItemFigures.Empty;
        decimal siteEquipment = 0;

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

        // A record with a supplier is supplier cost; one without (a cash purchase from custody, a site expense on the
        // item) is a direct cost. Each record is in exactly one of the two.
        foreach (var group in supplierRows.GroupBy(row => row.ItemId))
        {
            var withSupplier = group.Where(row => row.SupplierId is not null).ToList();
            var direct = group.Where(row => row.SupplierId is null).ToList();
            Put(group.Key, ItemFigures.Empty with
            {
                SupplierRecordCount = withSupplier.Count,
                SupplierCount = withSupplier.Select(row => row.SupplierId).Distinct().Count(),
                SupplierCost = withSupplier.Sum(row => row.Amount),
                SupplierPaid = withSupplier.Sum(row => row.Paid),
                DirectRecordCount = direct.Count,
                DirectCost = direct.Sum(row => row.Amount),
                DirectPaid = direct.Sum(row => row.Paid),
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

        // A machine's cost names one work item or none. With none it is a cost of the whole site: counted once, in
        // the project's other costs, and never offered as "not linked yet".
        foreach (var total in equipment ?? [])
        {
            if (total.ItemId is null)
            {
                siteEquipment += total.Cost;
                continue;
            }

            Put(total.ItemId, ItemFigures.Empty with
            {
                EquipmentRecordCount = total.RecordCount,
                EquipmentCount = total.PartyCount,
                EquipmentCost = total.Cost,
                EquipmentPaid = total.Paid,
            });
        }

        // A supplier on two items is one supplier of the project. Worker, subcontractor and machine counts come per
        // item from the other modules, so at project level they are the sum of the item counts.
        var totals = byItem.Values.Aggregate(ItemFigures.Empty, (sum, figures) => sum.Add(figures)) with
        {
            SupplierCount = supplierRows
                .Where(row => row.ItemId is not null && row.SupplierId is not null)
                .Select(row => row.SupplierId)
                .Distinct()
                .Count(),
        };

        return new ItemsLedgerResult(byItem, totals, unallocated, siteEquipment);
    }
}
