using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>One project item as the items screens list it, before any figures.</summary>
internal sealed record ItemHeader(
    int Id,
    string NameAr,
    string NameEn,
    string Category,
    string Unit,
    string? Description,
    string? Notes,
    ProjectItemStatus Status,
    DateTime? DeactivatedAt,
    int? DeactivatedBy,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    byte[] RowVersion,
    int? ParentItemId = null)
{
    public bool IsMainItem => ParentItemId is null;
}

/// <summary>Everything the items screens are computed from, loaded once per request.</summary>
/// <param name="Items">Every item of the project, active and inactive, by English name.</param>
/// <param name="Expenses">Every record of the project, flat (approved or not; the ledger picks the approved ones).</param>
/// <param name="Ledger">The figures per item, the totals and the not-linked group.</param>
internal sealed record ItemsLedgerData(
    IReadOnlyList<ItemHeader> Items,
    IReadOnlyList<ExpenseSettlementRow> Expenses,
    ItemsLedgerResult Ledger)
{
    /// <summary>The main items and their sub-items, built once from <see cref="Items"/>.</summary>
    public ItemTree Tree { get; } = ItemTree.Build(Items);

    /// <summary>One row of the items table for <paramref name="item"/>, with its own figures and, for a main item, its sub-items'.</summary>
    public ItemRow RowOf(ItemHeader item) => ItemRow.From(item, Ledger.Of(item.Id), Tree.SubItemIds(item.Id).Count, Tree.WithSubItems(item, Ledger));

    /// <summary>
    /// Approved site expenses with no supplier and no item: in the project total, but on no items screen.
    /// </summary>
    public decimal OtherSiteExpenses =>
        Expenses.Where(row => row.Status == ExpenseStatus.Approved && !ExpenseSettlement.IsSupplierSide(row)).Sum(row => row.Amount);
}

/// <summary>
/// Loads the flat rows of one project and runs <see cref="ItemsLedger"/> over them: the project's records from
/// this module, and the per-item totals that Workforce, Subcontractors and Fleet report through the contracts.
/// Projects never reads their tables.
/// </summary>
internal sealed class ItemsLedgerSource(
    ProjectsDbContext db, ILaborItemCosts labor, ISubcontractItemCosts subcontracts, IEquipmentItemCosts equipment)
{
    public async Task<ItemsLedgerData> LoadAsync(int projectId, CancellationToken cancellationToken)
    {
        var items = await ItemQueries.Headers(db, projectId).ToListAsync(cancellationToken);
        var expenses = await ExpenseQueries.SettlementRows(db, projectId).ToListAsync(cancellationToken);
        var subcontractTotals = await subcontracts.TotalsByItemAsync(projectId, cancellationToken);
        var laborTotals = await labor.TotalsByItemAsync(projectId, cancellationToken);
        var equipmentTotals = await equipment.TotalsByItemAsync(projectId, cancellationToken);

        var ledger = ItemsLedger.Compute(SupplierLedgerRow.From(expenses), subcontractTotals, laborTotals, equipmentTotals);
        return new ItemsLedgerData(items, expenses, ledger);
    }
}

/// <summary>The Projects module's own answer to <see cref="IProjectItemsSummary"/>, for the overview and the reports.</summary>
internal sealed class ProjectItemsSummaryProvider(ItemsLedgerSource source) : IProjectItemsSummary
{
    public async Task<ProjectItemsSummary> SummarizeAsync(int projectId, CancellationToken cancellationToken)
    {
        var data = await source.LoadAsync(projectId, cancellationToken);

        var rows = data.Items
            .Select(item =>
            {
                var figures = data.Ledger.Of(item.Id);
                return new ItemTotalsRow(item.Id, item.NameAr, item.NameEn, figures.TotalCost, figures.Paid, figures.Outstanding);
            })
            .ToList();

        return new ProjectItemsSummary(
            ItemsGroupTotals.From(data.Ledger.Totals),
            ItemsGroupTotals.From(data.Ledger.Unallocated),
            rows,
            data.Items.Count(item => item.Status == ProjectItemStatus.Active));
    }
}

/// <summary>The project item queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class ItemQueries
{
    /// <summary>Every item of a project, active and inactive, by English name.</summary>
    public static IQueryable<ItemHeader> Headers(ProjectsDbContext db, int projectId) =>
        Project(db.ProjectItems.AsNoTracking().Where(item => item.ProjectId == projectId));

    /// <summary>One item of a project, or none when the id belongs to another project.</summary>
    public static IQueryable<ItemHeader> One(ProjectsDbContext db, int projectId, int itemId) =>
        Project(db.ProjectItems.AsNoTracking().Where(item => item.ProjectId == projectId && item.Id == itemId));

    private static IQueryable<ItemHeader> Project(IQueryable<ProjectItem> items) =>
        items
            .OrderBy(item => item.NameEn)
            .ThenBy(item => item.Id)
            .Select(item => new ItemHeader(
                item.Id,
                item.NameAr,
                item.NameEn,
                item.Category,
                item.Unit,
                item.Description,
                item.Notes,
                item.Status,
                item.DeactivatedAt,
                item.DeactivatedBy,
                item.CreatedAt,
                item.CreatedBy,
                item.UpdatedAt,
                item.UpdatedBy,
                item.RowVersion,
                item.ParentItemId));

    /// <summary>True when any record of this module, approved or not, is linked to the item.</summary>
    public static IQueryable<ProjectExpense> LinkedExpenses(ProjectsDbContext db, int itemId) =>
        db.ProjectExpenses.AsNoTracking().Where(expense => expense.ProjectItemId == itemId);
}
