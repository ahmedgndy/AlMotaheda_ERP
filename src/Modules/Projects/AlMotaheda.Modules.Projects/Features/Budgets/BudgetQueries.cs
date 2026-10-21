using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Budgets;

/// <summary>One budget version without its lines.</summary>
internal sealed record BudgetHeader(
    int Id,
    int ProjectId,
    int Version,
    BudgetStatus Status,
    string? Title,
    string? Notes,
    DateTime? ApprovedAt,
    int? ApprovedBy,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    byte[] RowVersion);

/// <summary>One budget line with the name of its project item.</summary>
internal sealed record BudgetLineRow(
    int Id,
    int LineNo,
    string CostGroup,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    string Description,
    string? Unit,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal Amount);

/// <summary>The budget queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class BudgetQueries
{
    /// <summary>Every version of a project's budget, newest first.</summary>
    public static IQueryable<BudgetHeader> Headers(ProjectsDbContext db, int projectId) =>
        Project(db.Budgets.AsNoTracking().Where(budget => budget.ProjectId == projectId).OrderByDescending(budget => budget.Version));

    /// <summary>One version of the project, or none when the id belongs to another project.</summary>
    public static IQueryable<BudgetHeader> One(ProjectsDbContext db, int projectId, int budgetId) =>
        Project(db.Budgets.AsNoTracking().Where(budget => budget.ProjectId == projectId && budget.Id == budgetId));

    /// <summary>The approved version of the project (at most one, by a filtered unique index).</summary>
    public static IQueryable<BudgetHeader> Approved(ProjectsDbContext db, int projectId) =>
        Project(db.Budgets.AsNoTracking().Where(budget => budget.ProjectId == projectId && budget.Status == BudgetStatus.Approved));

    /// <summary>The amounts of the lines of some versions of the project, flat, for the totals.</summary>
    public static IQueryable<BudgetLineAmount> Amounts(ProjectsDbContext db, int projectId, IReadOnlyCollection<int> budgetIds) =>
        LinesOf(db, projectId, budgetIds)
            .Select(line => new BudgetLineAmount(line.BudgetId, line.CostGroup, line.ProjectItemId, line.Amount));

    /// <summary>
    /// The lines of these versions as entities, for a filter or a sum in SQL. EF cannot translate a <c>Where</c> or a
    /// <c>GroupBy</c> on the properties of the <see cref="BudgetLineAmount"/> record that <see cref="Amounts"/> makes.
    /// </summary>
    public static IQueryable<BudgetLine> LinesOf(ProjectsDbContext db, int projectId, IReadOnlyCollection<int> budgetIds) =>
        db.BudgetLines.AsNoTracking()
            .Where(line => line.Budget.ProjectId == projectId && budgetIds.Contains(line.BudgetId));

    /// <summary>The lines of one version, in line order, with item names.</summary>
    public static IQueryable<BudgetLineRow> Lines(ProjectsDbContext db, int projectId, int budgetId) =>
        db.BudgetLines.AsNoTracking()
            .Where(line => line.BudgetId == budgetId && line.Budget.ProjectId == projectId)
            .OrderBy(line => line.LineNo)
            .Select(line => new BudgetLineRow(
                line.Id,
                line.LineNo,
                line.CostGroup,
                line.ProjectItemId,
                line.ProjectItem != null ? line.ProjectItem.NameAr : null,
                line.ProjectItem != null ? line.ProjectItem.NameEn : null,
                line.Description,
                line.Unit,
                line.Quantity,
                line.UnitPrice,
                line.Amount));

    private static IQueryable<BudgetHeader> Project(IQueryable<Budget> budgets) =>
        budgets.Select(budget => new BudgetHeader(
            budget.Id,
            budget.ProjectId,
            budget.Version,
            budget.Status,
            budget.Title,
            budget.Notes,
            budget.ApprovedAt,
            budget.ApprovedBy,
            budget.CreatedAt,
            budget.CreatedBy,
            budget.UpdatedAt,
            budget.UpdatedBy,
            budget.RowVersion));
}
