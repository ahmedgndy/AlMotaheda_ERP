using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Budgets;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>The plan figures of every item of a project, per item (own), before any roll-up.</summary>
internal sealed record ItemPlanFigures(
    IReadOnlyDictionary<int, decimal> Budgeted,
    bool HasApprovedBudget,
    IReadOnlyDictionary<int, decimal> SubcontractCommitted,
    IReadOnlyDictionary<int, decimal> OrdersCommitted,
    IReadOnlyDictionary<int, int> PendingReview,
    IReadOnlyDictionary<int, int> PriceWarnings)
{
    /// <summary>
    /// The plan of a table row: the item's own figures, or for a main item its own plus its sub-items', next to the
    /// row's actual cost (<paramref name="actual"/>, the same scope).
    /// </summary>
    public ItemPlanView For(IReadOnlyList<int> scope, decimal actual)
    {
        decimal Sum(IReadOnlyDictionary<int, decimal> values) => scope.Sum(id => values.GetValueOrDefault(id));
        int Count(IReadOnlyDictionary<int, int> values) => scope.Sum(id => values.GetValueOrDefault(id));

        var budgeted = Sum(Budgeted);
        decimal? remaining = HasApprovedBudget && budgeted > 0 ? budgeted - actual : null;
        return new ItemPlanView(
            budgeted, remaining, remaining < 0, Sum(SubcontractCommitted), Sum(OrdersCommitted), Count(PendingReview), Count(PriceWarnings));
    }
}

/// <summary>
/// Loads the plan figures of a project's items: one query or call per source (approved budget lines, subcontract
/// commitments, purchase order commitments, records waiting for review, price warnings), never one per item.
/// </summary>
internal sealed class ItemPlans(ProjectsDbContext db, ISubcontractItemCosts subcontracts, ISupplierOrders orders)
{
    public async Task<ItemPlanFigures> LoadAsync(int projectId, IReadOnlyList<int> itemIds, CancellationToken cancellationToken)
    {
        var approved = await BudgetQueries.Approved(db, projectId).Select(budget => (int?)budget.Id).FirstOrDefaultAsync(cancellationToken);
        var budgeted = approved is { } budgetId
            ? await BudgetQueries.Amounts(db, projectId, [budgetId])
                .Where(line => line.ProjectItemId != null)
                .GroupBy(line => line.ProjectItemId!.Value)
                .Select(group => new { group.Key, Amount = group.Sum(line => line.Amount) })
                .ToDictionaryAsync(entry => entry.Key, entry => entry.Amount, cancellationToken)
            : [];

        var commitments = (await subcontracts.CommitmentsByItemAsync(projectId, itemIds, cancellationToken))
            .GroupBy(line => line.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Remaining));

        var orderLines = await orders.LinesAsync(projectId, null, cancellationToken);
        var ordered = orderLines.Where(line => line.ProjectItemId is not null)
            .GroupBy(line => line.ProjectItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Committed));

        var pending = await db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.ProjectId == projectId && expense.ProjectItemId != null
                              && (expense.Status == ExpenseStatus.Recorded || expense.Status == ExpenseStatus.Returned))
            .GroupBy(expense => expense.ProjectItemId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Count, cancellationToken);

        var warnings = await db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.ProjectId == projectId && expense.ProjectItemId != null
                              && expense.PriceCheck == PriceCheckResult.AboveRange && expense.Status != ExpenseStatus.Rejected)
            .GroupBy(expense => expense.ProjectItemId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Count, cancellationToken);

        return new ItemPlanFigures(budgeted, approved is not null, commitments, ordered, pending, warnings);
    }
}
