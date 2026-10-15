using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Features.Reports;

namespace AlMotaheda.Modules.Projects.Features.Budgets;

/// <summary>One budget line as every calculation sees it.</summary>
internal sealed record BudgetLineAmount(int BudgetId, string CostGroup, int? ProjectItemId, decimal Amount);

/// <summary>The money of one cost group from its source records (expenses here, or another module's figures).</summary>
/// <param name="Recognized">Approved cost only.</param>
/// <param name="PendingReview">Waiting for review; never inside <paramref name="Recognized"/>.</param>
/// <param name="Committed">Signed but not executed yet (subcontracts only; 0 elsewhere).</param>
internal sealed record CostGroupActuals(string CostGroup, decimal Recognized, decimal Paid, decimal PendingReview, decimal Committed)
{
    public static CostGroupActuals Empty(string costGroup) => new(costGroup, 0, 0, 0, 0);

    public static CostGroupActuals From(string costGroup, ProjectCostFigures figures) =>
        new(costGroup, figures.Recognized, figures.Paid, figures.PendingReview, figures.Committed);

    public CostGroupActuals Add(CostGroupActuals other) => this with
    {
        Recognized = Recognized + other.Recognized,
        Paid = Paid + other.Paid,
        PendingReview = PendingReview + other.PendingReview,
        Committed = Committed + other.Committed,
    };
}

/// <summary>The budget of one cost group against its actual figures.</summary>
/// <param name="Variance">Budgeted − recognized. Negative means over budget.</param>
/// <param name="VariancePercent">Variance ÷ budgeted × 100, to two decimals; <c>null</c> when nothing is budgeted.</param>
internal sealed record GroupComparison(
    string CostGroup,
    decimal Budgeted,
    decimal Recognized,
    decimal Variance,
    decimal? VariancePercent,
    decimal Paid,
    decimal Outstanding,
    decimal PendingReview,
    decimal Committed);

/// <summary>The same figures over every cost group.</summary>
internal sealed record ComparisonTotals(
    decimal Budgeted,
    decimal Recognized,
    decimal Variance,
    decimal? VariancePercent,
    decimal Paid,
    decimal Outstanding,
    decimal PendingReview,
    decimal Committed);

/// <summary>
/// The budget lines of one project item against the item's recognized cost from the items ledger. Rows are never
/// added up into one "items total": budget lines without an item and costs not linked to an item would be missing.
/// </summary>
internal sealed record ItemComparison(
    int ItemId,
    string NameAr,
    string NameEn,
    decimal Budgeted,
    decimal Recognized,
    decimal Variance,
    decimal? VariancePercent,
    decimal Paid,
    decimal Outstanding);

/// <summary>One amount per cost group, in the order of <see cref="CostGroups.All"/>.</summary>
internal sealed record CostGroupAmount(string CostGroup, decimal Amount);

/// <summary>
/// Budget against actual (02-projects-planning.md, section A). Pure: the handler loads flat rows and calls this,
/// so the arithmetic is tested to the piastre without a database.
/// </summary>
/// <remarks>
/// Recognized cost is only ever approved cost. Pending review, payments and commitments are shown next to it and
/// never added into it.
/// </remarks>
internal static class BudgetComparison
{
    /// <summary>
    /// The cost group of an expense record: a supply is <c>Materials</c>; a site expense is <c>SiteExpenses</c>,
    /// except the categories <c>Other</c> and <c>PettyCash</c>, which are <c>Other</c>.
    /// </summary>
    public static string GroupOf(ExpenseKind kind, ExpenseCategory? category) =>
        kind == ExpenseKind.Supply ? CostGroups.Materials
        : category is ExpenseCategory.Other or ExpenseCategory.PettyCash ? CostGroups.Other
        : CostGroups.SiteExpenses;

    /// <summary>
    /// The three groups that come from this module's expense records. Recognized and paid follow
    /// <see cref="ExpenseSettlement"/>; pending review is the amount of records still <c>Recorded</c>. Returned and
    /// rejected records count nowhere.
    /// </summary>
    public static IReadOnlyList<CostGroupActuals> FromExpenses(IEnumerable<ExpenseSettlementRow> rows) =>
        // One rule with the overview and the cost summary.
        ProjectCosts.FromExpenses(rows).Select(group => group.ToActuals()).ToList();

    /// <summary>The amount of each cost group in the lines, with every group present (0 when it has no line).</summary>
    public static IReadOnlyList<CostGroupAmount> GroupTotals(IEnumerable<BudgetLineAmount> lines)
    {
        var sums = CostGroups.All.ToDictionary(group => group, _ => 0m);
        foreach (var line in lines)
        {
            sums[line.CostGroup] = sums.GetValueOrDefault(line.CostGroup) + line.Amount;
        }

        return CostGroups.All.Select(group => new CostGroupAmount(group, sums[group])).ToList();
    }

    /// <summary>
    /// One row per cost group (every group, in the order of <see cref="CostGroups.All"/>), and the totals.
    /// <paramref name="actuals"/> may hold several entries for one group (two sources); they are added.
    /// A group with no source is zero.
    /// </summary>
    public static (IReadOnlyList<GroupComparison> Groups, ComparisonTotals Totals) ByGroup(
        IEnumerable<BudgetLineAmount> lines, IEnumerable<CostGroupActuals> actuals)
    {
        var budgeted = GroupTotals(lines).ToDictionary(total => total.CostGroup, total => total.Amount);

        var actual = CostGroups.All.ToDictionary(group => group, CostGroupActuals.Empty);
        foreach (var entry in actuals)
        {
            if (actual.TryGetValue(entry.CostGroup, out var sum))
            {
                actual[entry.CostGroup] = sum.Add(entry);
            }
        }

        var groups = CostGroups.All
            .Select(group =>
            {
                var a = actual[group];
                var variance = budgeted[group] - a.Recognized;
                return new GroupComparison(
                    group,
                    budgeted[group],
                    a.Recognized,
                    variance,
                    VariancePercent(budgeted[group], variance),
                    a.Paid,
                    a.Recognized - a.Paid,
                    a.PendingReview,
                    a.Committed);
            })
            .ToList();

        var totalBudgeted = groups.Sum(group => group.Budgeted);
        var totalRecognized = groups.Sum(group => group.Recognized);
        var totalPaid = groups.Sum(group => group.Paid);
        var totalVariance = totalBudgeted - totalRecognized;

        var totals = new ComparisonTotals(
            totalBudgeted,
            totalRecognized,
            totalVariance,
            VariancePercent(totalBudgeted, totalVariance),
            totalPaid,
            totalRecognized - totalPaid,
            groups.Sum(group => group.PendingReview),
            groups.Sum(group => group.Committed));

        return (groups, totals);
    }

    /// <summary>
    /// Budget lines that name a project item, summed per item, against the item's recognized cost from the items
    /// ledger (<see cref="IProjectItemsSummary"/>). An item appears when it has a budget line or a cost; items are
    /// in the order of <paramref name="items"/>.
    /// </summary>
    public static IReadOnlyList<ItemComparison> ByItem(IEnumerable<BudgetLineAmount> lines, IReadOnlyList<ItemTotalsRow> items)
    {
        var budgeted = lines
            .Where(line => line.ProjectItemId is not null)
            .GroupBy(line => line.ProjectItemId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Amount));

        return items
            .Where(item => budgeted.ContainsKey(item.ItemId) || item.TotalCost != 0 || item.Paid != 0)
            .Select(item =>
            {
                var planned = budgeted.GetValueOrDefault(item.ItemId);
                var variance = planned - item.TotalCost;
                return new ItemComparison(
                    item.ItemId,
                    item.NameAr,
                    item.NameEn,
                    planned,
                    item.TotalCost,
                    variance,
                    VariancePercent(planned, variance),
                    item.Paid,
                    item.Outstanding);
            })
            .ToList();
    }

    /// <summary>Variance ÷ budgeted × 100, rounded to two decimals; <c>null</c> when nothing is budgeted.</summary>
    public static decimal? VariancePercent(decimal budgeted, decimal variance) =>
        budgeted == 0 ? null : Math.Round(variance / budgeted * 100, 2, MidpointRounding.AwayFromZero);
}
