using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Budgets;
using AlMotaheda.Modules.Projects.Features.Expenses;

namespace AlMotaheda.Modules.Projects.Features.Reports;

/// <summary>
/// The actual money of one cost group of a project. Recognized, paid, pending review and committed are four
/// different facts: they are shown side by side and never added into each other.
/// </summary>
/// <param name="HasSource">
/// False when no module reports this group (for example the Workforce module is not installed). Its figures are
/// then zero because nothing is known, not because nothing was spent.
/// </param>
/// <param name="Recognized">Approved cost only.</param>
/// <param name="Paid">The part of <paramref name="Recognized"/> settled by payments (or paid from custody).</param>
/// <param name="RecognizedRecords">How many approved records make up <paramref name="Recognized"/>.</param>
/// <param name="PendingReview">Recorded or submitted, not approved yet. Never inside <paramref name="Recognized"/>.</param>
/// <param name="PendingRecords">How many records wait for review.</param>
/// <param name="Committed">Signed but not executed or invoiced yet (subcontracts, and open purchase orders in Materials; 0 elsewhere).</param>
internal sealed record CostGroupFigures(
    string CostGroup,
    bool HasSource,
    decimal Recognized,
    decimal Paid,
    int RecognizedRecords,
    decimal PendingReview,
    int PendingRecords,
    decimal Committed)
{
    /// <summary>Recognized − paid. Always calculated, never stored.</summary>
    public decimal Outstanding => Recognized - Paid;

    public static CostGroupFigures Empty(string costGroup, bool hasSource) => new(costGroup, hasSource, 0, 0, 0, 0, 0, 0);

    /// <summary>The same figures as the budget comparison takes them.</summary>
    public CostGroupActuals ToActuals() => new(CostGroup, Recognized, Paid, PendingReview, Committed);

    public CostGroupFigures Add(ProjectCostFigures figures) => this with
    {
        HasSource = true,
        Recognized = Recognized + figures.Recognized,
        Paid = Paid + figures.Paid,
        RecognizedRecords = RecognizedRecords + figures.RecognizedRecords,
        PendingReview = PendingReview + figures.PendingReview,
        PendingRecords = PendingRecords + figures.PendingRecords,
        Committed = Committed + figures.Committed,
    };
}

/// <summary>The figures of every cost group added up. Each fact is added only to itself.</summary>
internal sealed record CostTotals(
    decimal Recognized,
    decimal Paid,
    decimal Outstanding,
    int RecognizedRecords,
    decimal PendingReview,
    int PendingRecords,
    decimal Committed);

/// <summary>One cost source's answer, as the handler collected it.</summary>
internal sealed record SourceFigures(string CostGroup, ProjectCostFigures Figures);

/// <summary>
/// The actual cost of a project per cost group (02-projects-planning.md, sections A and E). Pure: the handlers load
/// flat rows and the other modules' figures, then call this, so the overview, the budget comparison and the cost
/// summary all use one rule.
/// </summary>
/// <remarks>
/// <para><c>Materials</c>, <c>SiteExpenses</c> and <c>Other</c> come from this module's expense records
/// (<see cref="BudgetComparison.GroupOf"/>). <c>Labor</c>, <c>Subcontractors</c>, <c>Equipment</c> and <c>Staff</c> come only from
/// the <see cref="IProjectCostSource"/> of the module that owns them; this module never reads their tables.</para>
/// <para>Only approved records are cost. A returned or rejected record counts nowhere.</para>
/// </remarks>
internal static class ProjectCosts
{
    /// <summary>The groups owned by other modules and reported through <see cref="IProjectCostSource"/>.</summary>
    public static IReadOnlyList<string> SourceGroups { get; } = [CostGroups.Labor, CostGroups.Subcontractors, CostGroups.Equipment, CostGroups.Staff];

    /// <summary>
    /// The three groups of this module's expense records, in the order of <see cref="CostGroups.All"/>.
    /// Recognized and paid follow <see cref="ExpenseSettlement"/>; pending review is the records still
    /// <c>Recorded</c>.
    /// </summary>
    public static IReadOnlyList<CostGroupFigures> FromExpenses(IEnumerable<ExpenseSettlementRow> rows)
    {
        var groups = new Dictionary<string, CostGroupFigures>
        {
            [CostGroups.Materials] = CostGroupFigures.Empty(CostGroups.Materials, hasSource: true),
            [CostGroups.SiteExpenses] = CostGroupFigures.Empty(CostGroups.SiteExpenses, hasSource: true),
            [CostGroups.Other] = CostGroupFigures.Empty(CostGroups.Other, hasSource: true),
        };

        foreach (var row in rows)
        {
            var group = BudgetComparison.GroupOf(row.Kind, row.Category);
            var approved = row.Status == ExpenseStatus.Approved;
            var pending = row.Status == ExpenseStatus.Recorded;

            groups[group] = groups[group].Add(new ProjectCostFigures(
                ExpenseSettlement.Recognized(row),
                ExpenseSettlement.Paid(row),
                approved ? 1 : 0,
                pending ? row.Amount : 0,
                pending ? 1 : 0,
                0));
        }

        return CostGroups.All.Where(groups.ContainsKey).Select(group => groups[group]).ToList();
    }

    /// <summary>
    /// Every cost group, in the order of <see cref="CostGroups.All"/>: this module's groups from
    /// <paramref name="expenses"/>, the others from <paramref name="sources"/>. A source for a group this module
    /// owns is ignored (those records are counted here already), except the committed cost of open purchase orders
    /// (Procurement), which is added to Materials: invoiced purchases are already this module's supply records.
    /// Two sources of one group are added. A group with no source is zero and says so
    /// (<see cref="CostGroupFigures.HasSource"/>).
    /// </summary>
    public static IReadOnlyList<CostGroupFigures> ByGroup(IEnumerable<ExpenseSettlementRow> expenses, IEnumerable<SourceFigures> sources)
    {
        var groups = FromExpenses(expenses).ToDictionary(group => group.CostGroup);
        foreach (var group in SourceGroups)
        {
            groups[group] = CostGroupFigures.Empty(group, hasSource: false);
        }

        foreach (var source in sources)
        {
            if (SourceGroups.Contains(source.CostGroup))
            {
                groups[source.CostGroup] = groups[source.CostGroup].Add(source.Figures);
            }
            else if (source.CostGroup == CostGroups.Materials)
            {
                groups[CostGroups.Materials] = groups[CostGroups.Materials].Add(
                    ProjectCostFigures.Empty with { Committed = source.Figures.Committed });
            }
        }

        return CostGroups.All.Select(group => groups[group]).ToList();
    }

    public static CostTotals Total(IEnumerable<CostGroupFigures> groups)
    {
        var list = groups.ToList();
        var recognized = list.Sum(group => group.Recognized);
        var paid = list.Sum(group => group.Paid);
        return new CostTotals(
            recognized,
            paid,
            recognized - paid,
            list.Sum(group => group.RecognizedRecords),
            list.Sum(group => group.PendingReview),
            list.Sum(group => group.PendingRecords),
            list.Sum(group => group.Committed));
    }
}
