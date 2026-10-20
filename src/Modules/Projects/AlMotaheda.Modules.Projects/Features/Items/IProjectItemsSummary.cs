namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>
/// The project items figures for the overview and the reports, from the same <see cref="ItemsLedger"/> as the
/// items screens, so the overview card and the items table can never disagree.
/// </summary>
internal interface IProjectItemsSummary
{
    /// <summary>Items totals (all items), the not-linked group, and per-item totals.</summary>
    Task<ProjectItemsSummary> SummarizeAsync(int projectId, CancellationToken cancellationToken);
}

/// <param name="Totals">The sums over every item of the project, active and inactive.</param>
/// <param name="Unallocated">Approved records with no item yet. Never inside <paramref name="Totals"/>.</param>
/// <param name="Items">One row per item of the project, including items with no records yet.</param>
internal sealed record ProjectItemsSummary(ItemsGroupTotals Totals, ItemsGroupTotals Unallocated, IReadOnlyList<ItemTotalsRow> Items, int ActiveItemCount);

internal sealed record ItemsGroupTotals(
    decimal SupplierCost, decimal SubcontractorCost, decimal LaborCost, decimal TotalCost, decimal Paid, decimal Outstanding, int RecordCount,
    decimal EquipmentCost = 0)
{
    public static ItemsGroupTotals From(ItemFigures figures) => new(
        figures.SupplierCost,
        figures.SubcontractorCost,
        figures.LaborCost,
        figures.TotalCost,
        figures.Paid,
        figures.Outstanding,
        figures.RecordCount,
        figures.EquipmentCost);
}

internal sealed record ItemTotalsRow(int ItemId, string NameAr, string NameEn, decimal TotalCost, decimal Paid, decimal Outstanding);
