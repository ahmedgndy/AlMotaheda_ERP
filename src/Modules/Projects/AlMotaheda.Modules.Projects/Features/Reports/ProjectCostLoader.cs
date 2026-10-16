using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Reports;

/// <summary>What the cost screens are computed from, loaded once per request.</summary>
/// <param name="Expenses">This module's records of the project (in the period, when one is given), flat.</param>
/// <param name="Groups">Every cost group, from <see cref="ProjectCosts.ByGroup"/>.</param>
internal sealed record ProjectCostData(IReadOnlyList<ExpenseSettlementRow> Expenses, IReadOnlyList<CostGroupFigures> Groups);

/// <summary>
/// Loads the actual cost of one project: this module's expense records with one query, and the labor,
/// subcontract and equipment figures from the <see cref="IProjectCostSource"/> registered by the modules that own
/// them. Any of those modules may be missing; then its group is zero and marked as having no source.
/// </summary>
internal sealed class ProjectCostLoader(ProjectsDbContext db, IEnumerable<IProjectCostSource> sources)
{
    /// <param name="from">First business date included; <c>null</c> for no limit.</param>
    /// <param name="to">Last business date included; <c>null</c> for no limit.</param>
    public async Task<ProjectCostData> LoadAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var expenses = await ReportQueries.SettlementRows(db, projectId, from, to).ToListAsync(cancellationToken);

        var figures = new List<SourceFigures>();
        foreach (var source in sources)
        {
            // Groups owned by this module are counted from its own records, never from a source.
            if (ProjectCosts.SourceGroups.Contains(source.Group))
            {
                figures.Add(new SourceFigures(source.Group, await source.SummarizeAsync(projectId, from, to, cancellationToken)));
            }
        }

        return new ProjectCostData(expenses, ProjectCosts.ByGroup(expenses, figures));
    }
}

/// <summary>The report queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class ReportQueries
{
    /// <summary>
    /// The project's records as every calculation sees them. Without a period this is exactly
    /// <see cref="ExpenseQueries.SettlementRows(ProjectsDbContext, int)"/>. With a period it keeps the records whose
    /// business date (<c>ExpenseDate</c>) is inside it; what is paid on them is counted whenever it was paid.
    /// </summary>
    public static IQueryable<ExpenseSettlementRow> SettlementRows(ProjectsDbContext db, int projectId, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
        {
            return ExpenseQueries.SettlementRows(db, projectId);
        }

        var expenses = db.ProjectExpenses.AsNoTracking().Where(expense => expense.ProjectId == projectId);
        if (from is { } first)
        {
            expenses = expenses.Where(expense => expense.ExpenseDate >= first);
        }

        if (to is { } last)
        {
            expenses = expenses.Where(expense => expense.ExpenseDate <= last);
        }

        // The same columns as ExpenseQueries.SettlementRows: allocations of payments that are not voided.
        return expenses.Select(expense => new ExpenseSettlementRow(
            expense.Id,
            expense.Kind,
            expense.Status,
            expense.Channel,
            expense.Category,
            expense.SupplierId,
            expense.ProjectItemId,
            // The adjusted amount, like ExpenseQueries.SettlementRows.
            expense.Amount + (expense.Adjustments.Where(adjustment => adjustment.Status == AdjustmentStatus.Approved)
                .Sum(adjustment => (decimal?)adjustment.Amount) ?? 0),
            db.SupplierPaymentAllocations
                .Where(allocation => allocation.ExpenseId == expense.Id && allocation.Payment.VoidedAt == null)
                .Sum(allocation => allocation.Amount)));
    }

    /// <summary>The names of some suppliers, for the obligations report. One query, never one per row.</summary>
    public static IQueryable<SupplierName> SupplierNames(ProjectsDbContext db, IReadOnlyCollection<int> supplierIds) =>
        db.Suppliers.AsNoTracking()
            .Where(supplier => supplierIds.Contains(supplier.Id))
            .Select(supplier => new SupplierName(supplier.Id, supplier.NameAr, supplier.NameEn, supplier.Status));
}

internal sealed record SupplierName(int Id, string NameAr, string NameEn, SupplierStatus Status);
