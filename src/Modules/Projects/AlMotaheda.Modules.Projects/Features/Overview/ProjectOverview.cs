using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Features.Budgets;
using AlMotaheda.Modules.Projects.Features.Reports;

namespace AlMotaheda.Modules.Projects.Features.Overview;

/// <summary>One cost group on the overview card.</summary>
/// <param name="Budgeted">The approved budget of the group; <c>null</c> when the project has no approved budget.</param>
/// <param name="Remaining">Budgeted − recognized (negative = over budget); <c>null</c> without an approved budget.</param>
/// <param name="PercentUsed">Recognized ÷ budgeted × 100; <c>null</c> without a budget or when nothing is budgeted.</param>
internal sealed record OverviewGroup(
    string CostGroup,
    bool HasSource,
    decimal? Budgeted,
    decimal Recognized,
    decimal Paid,
    decimal Outstanding,
    decimal PendingReview,
    decimal Committed,
    decimal? Remaining,
    decimal? PercentUsed);

/// <summary>The same figures over the whole project. Estimate, commitment, cost and payment stay separate.</summary>
internal sealed record OverviewTotals(
    decimal? Budget,
    decimal Recognized,
    decimal Paid,
    decimal Outstanding,
    decimal PendingReview,
    decimal Committed,
    decimal? RemainingBudget,
    decimal? PercentUsed);

/// <summary>
/// The overview figures (02-projects-planning.md, section E). Pure. The budget per group comes from
/// <see cref="BudgetComparison.GroupTotals"/> and the cost per group from <see cref="ProjectCosts"/>, the same
/// classes as the budget comparison and the cost summary, so the screens can never disagree.
/// </summary>
/// <remarks>
/// Nothing is ever added across the four money facts: the budget is a plan, the commitment is signed but not
/// executed, the recognized cost is approved records, and paid is what payments settled. "Remaining budget" is the
/// budget minus recognized cost only: pending records and commitments are shown next to it, not taken from it.
/// </remarks>
internal static class ProjectOverview
{
    /// <param name="approvedLines">The lines of the approved budget, or <c>null</c> when the project has none.</param>
    /// <param name="groups">Every cost group, from <see cref="ProjectCosts.ByGroup"/>.</param>
    public static (IReadOnlyList<OverviewGroup> Groups, OverviewTotals Totals) Compute(
        IReadOnlyList<BudgetLineAmount>? approvedLines, IReadOnlyList<CostGroupFigures> groups)
    {
        var budgeted = approvedLines is null
            ? null
            : BudgetComparison.GroupTotals(approvedLines).ToDictionary(total => total.CostGroup, total => total.Amount);

        var rows = groups
            .Select(group =>
            {
                decimal? planned = budgeted?.GetValueOrDefault(group.CostGroup);
                return new OverviewGroup(
                    group.CostGroup,
                    group.HasSource,
                    planned,
                    group.Recognized,
                    group.Paid,
                    group.Outstanding,
                    group.PendingReview,
                    group.Committed,
                    planned - group.Recognized,
                    PercentUsed(planned, group.Recognized));
            })
            .ToList();

        var costs = ProjectCosts.Total(groups);
        decimal? budget = budgeted?.Values.Sum();
        var totals = new OverviewTotals(
            budget,
            costs.Recognized,
            costs.Paid,
            costs.Outstanding,
            costs.PendingReview,
            costs.Committed,
            budget - costs.Recognized,
            PercentUsed(budget, costs.Recognized));

        return (rows, totals);
    }

    /// <summary>Recognized ÷ budget × 100, to two decimals; <c>null</c> without a budget or when it is zero.</summary>
    public static decimal? PercentUsed(decimal? budget, decimal recognized) =>
        budget is null or 0 ? null : Money.Round(recognized / budget.Value * 100);
}
