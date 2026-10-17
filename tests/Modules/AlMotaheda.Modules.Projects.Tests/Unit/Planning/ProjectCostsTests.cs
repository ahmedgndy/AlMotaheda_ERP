using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Budgets;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Overview;
using AlMotaheda.Modules.Projects.Features.Reports;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Planning;

/// <summary>
/// The cost per group, the overview and the obligations, to the piastre, with real numbers. Estimate, commitment,
/// recognized cost and payments must stay separate, and a missing cost source must give zero, not an error.
/// </summary>
public sealed class ProjectCostsTests
{
    /// <summary>Supplies and expenses: approved, waiting, returned and rejected, on custody and on credit.</summary>
    private static readonly ExpenseSettlementRow[] Expenses =
    [
        Row(1, ExpenseKind.Supply, null, ExpenseStatus.Approved, PaymentChannel.Custody, supplierId: 1, 45_000.50m, allocated: 0),
        Row(2, ExpenseKind.Supply, null, ExpenseStatus.Approved, PaymentChannel.Credit, supplierId: 1, 20_000m, allocated: 5_000m),
        Row(3, ExpenseKind.Supply, null, ExpenseStatus.Recorded, PaymentChannel.Credit, supplierId: 2, 7_000m, allocated: 0),
        Row(4, ExpenseKind.Supply, null, ExpenseStatus.Returned, PaymentChannel.Custody, supplierId: 1, 3_000m, allocated: 0),
        Row(5, ExpenseKind.Expense, ExpenseCategory.Lab, ExpenseStatus.Approved, PaymentChannel.Custody, supplierId: null, 2_500m, allocated: 0),
        Row(6, ExpenseKind.Expense, ExpenseCategory.PettyCash, ExpenseStatus.Approved, PaymentChannel.Custody, supplierId: null, 800m, allocated: 0),
        Row(7, ExpenseKind.Expense, ExpenseCategory.Other, ExpenseStatus.Approved, PaymentChannel.Credit, supplierId: 3, 1_200m, allocated: 0),
        Row(8, ExpenseKind.Expense, ExpenseCategory.Guards, ExpenseStatus.Rejected, PaymentChannel.Custody, supplierId: null, 9_999m, allocated: 0),
    ];

    private static readonly SourceFigures[] Sources =
    [
        new(CostGroups.Labor, new ProjectCostFigures(55_000m, 40_000m, 12, 3_000m, 2, 0)),
        new(CostGroups.Subcontractors, new ProjectCostFigures(120_000m, 100_000m, 4, 0, 0, 80_000m)),

        // A source must never report a group this module owns: its records are counted here already.
        new(CostGroups.Materials, new ProjectCostFigures(1_000_000m, 1_000_000m, 1, 0, 0, 0)),
    ];

    private static readonly BudgetLineAmount[] BudgetLines =
    [
        new(1, CostGroups.Materials, ProjectItemId: 5, 60_000m),
        new(1, CostGroups.Materials, ProjectItemId: null, 40_000m),
        new(1, CostGroups.Labor, ProjectItemId: null, 50_000m),
        new(1, CostGroups.Subcontractors, ProjectItemId: 6, 200_000m),
        new(1, CostGroups.Equipment, ProjectItemId: null, 30_000m),
        new(1, CostGroups.SiteExpenses, ProjectItemId: null, 10_000m),
    ];

    [Fact]
    public void Materials_takes_only_the_committed_cost_of_open_purchase_orders_from_a_source()
    {
        // Procurement reports open purchase orders; invoiced purchases are already this module's supply records.
        var groups = ProjectCosts.ByGroup(Expenses, [new SourceFigures(CostGroups.Materials, new ProjectCostFigures(1_000_000m, 1_000_000m, 1, 500m, 1, 9_000m))]);

        var materials = Group(groups, CostGroups.Materials);
        Assert.Equal((65_000.50m, 50_000.50m, 7_000m, 9_000m), (materials.Recognized, materials.Paid, materials.PendingReview, materials.Committed));
    }

    [Fact]
    public void Every_group_counts_only_approved_records_and_keeps_pending_apart()
    {
        var groups = ProjectCosts.ByGroup(Expenses, Sources);

        Assert.Equal(CostGroups.All, groups.Select(group => group.CostGroup));

        var materials = Group(groups, CostGroups.Materials);
        Assert.True(materials.HasSource);
        Assert.Equal(65_000.50m, materials.Recognized);   // the source's 1,000,000 is ignored
        Assert.Equal(50_000.50m, materials.Paid);         // custody at approval + 5,000 allocated on credit
        Assert.Equal(15_000m, materials.Outstanding);
        Assert.Equal(2, materials.RecognizedRecords);
        Assert.Equal(7_000m, materials.PendingReview);    // the returned supply counts nowhere
        Assert.Equal(1, materials.PendingRecords);
        Assert.Equal(0m, materials.Committed);

        var site = Group(groups, CostGroups.SiteExpenses);
        Assert.Equal(2_500m, site.Recognized);            // the rejected guards expense is never a cost
        Assert.Equal(2_500m, site.Paid);
        Assert.Equal(1, site.RecognizedRecords);
        Assert.Equal(0, site.PendingRecords);

        var other = Group(groups, CostGroups.Other);
        Assert.Equal(2_000m, other.Recognized);
        Assert.Equal(800m, other.Paid);
        Assert.Equal(1_200m, other.Outstanding);
        Assert.Equal(2, other.RecognizedRecords);

        var labor = Group(groups, CostGroups.Labor);
        Assert.True(labor.HasSource);
        Assert.Equal(55_000m, labor.Recognized);
        Assert.Equal(40_000m, labor.Paid);
        Assert.Equal(15_000m, labor.Outstanding);
        Assert.Equal(12, labor.RecognizedRecords);
        Assert.Equal(3_000m, labor.PendingReview);
        Assert.Equal(2, labor.PendingRecords);

        var subcontractors = Group(groups, CostGroups.Subcontractors);
        Assert.Equal(120_000m, subcontractors.Recognized);
        Assert.Equal(20_000m, subcontractors.Outstanding);
        Assert.Equal(80_000m, subcontractors.Committed);

        var equipment = Group(groups, CostGroups.Equipment);
        Assert.False(equipment.HasSource);
        Assert.Equal(0m, equipment.Recognized);
        Assert.Equal(0m, equipment.Paid);
    }

    [Fact]
    public void The_totals_add_each_fact_only_to_itself()
    {
        var totals = ProjectCosts.Total(ProjectCosts.ByGroup(Expenses, Sources));

        Assert.Equal(244_500.50m, totals.Recognized);
        Assert.Equal(193_300.50m, totals.Paid);
        Assert.Equal(51_200m, totals.Outstanding);
        Assert.Equal(21, totals.RecognizedRecords);
        Assert.Equal(10_000m, totals.PendingReview);
        Assert.Equal(3, totals.PendingRecords);
        Assert.Equal(80_000m, totals.Committed);
    }

    [Fact]
    public void With_no_cost_source_registered_the_three_other_groups_are_zero_and_say_so()
    {
        var groups = ProjectCosts.ByGroup(Expenses, []);

        foreach (var group in ProjectCosts.SourceGroups)
        {
            var figures = Group(groups, group);
            Assert.False(figures.HasSource);
            Assert.Equal(0m, figures.Recognized);
            Assert.Equal(0m, figures.Paid);
            Assert.Equal(0m, figures.PendingReview);
            Assert.Equal(0m, figures.Committed);
            Assert.Equal(0, figures.RecognizedRecords);
        }

        Assert.Equal(69_500.50m, ProjectCosts.Total(groups).Recognized);
    }

    [Fact]
    public void Two_sources_of_one_group_are_added()
    {
        var groups = ProjectCosts.ByGroup(
            [],
            [
                new(CostGroups.Equipment, new ProjectCostFigures(1_000m, 400m, 2, 100m, 1, 0)),
                new(CostGroups.Equipment, new ProjectCostFigures(500.25m, 500.25m, 1, 0, 0, 0)),
            ]);

        var equipment = Group(groups, CostGroups.Equipment);
        Assert.Equal(1_500.25m, equipment.Recognized);
        Assert.Equal(900.25m, equipment.Paid);
        Assert.Equal(600m, equipment.Outstanding);
        Assert.Equal(3, equipment.RecognizedRecords);
        Assert.Equal(100m, equipment.PendingReview);
        Assert.Equal(1, equipment.PendingRecords);
    }

    [Fact]
    public void The_budget_comparison_and_the_cost_figures_use_the_same_expense_rule()
    {
        var fromComparison = BudgetComparison.FromExpenses(Expenses);
        var fromCosts = ProjectCosts.FromExpenses(Expenses).Select(group => group.ToActuals());

        Assert.Equal(fromCosts, fromComparison);
    }

    [Fact]
    public void The_overview_compares_the_approved_budget_with_recognized_cost_only()
    {
        var (groups, totals) = ProjectOverview.Compute(BudgetLines, ProjectCosts.ByGroup(Expenses, Sources));

        Assert.Equal(390_000m, totals.Budget);
        Assert.Equal(244_500.50m, totals.Recognized);
        Assert.Equal(193_300.50m, totals.Paid);
        Assert.Equal(51_200m, totals.Outstanding);
        Assert.Equal(10_000m, totals.PendingReview);
        Assert.Equal(80_000m, totals.Committed);
        Assert.Equal(145_499.50m, totals.RemainingBudget);   // pending and committed are not taken from it
        Assert.Equal(62.69m, totals.PercentUsed);             // 62.6924…

        var materials = groups.Single(group => group.CostGroup == CostGroups.Materials);
        Assert.Equal(100_000m, materials.Budgeted);
        Assert.Equal(34_999.50m, materials.Remaining);
        Assert.Equal(65.00m, materials.PercentUsed);          // 65.0005

        var labor = groups.Single(group => group.CostGroup == CostGroups.Labor);
        Assert.Equal(-5_000m, labor.Remaining);               // over budget
        Assert.Equal(110.00m, labor.PercentUsed);

        // Nothing budgeted for Other: the remaining is negative and no percent is invented.
        var other = groups.Single(group => group.CostGroup == CostGroups.Other);
        Assert.Equal(0m, other.Budgeted);
        Assert.Equal(-2_000m, other.Remaining);
        Assert.Null(other.PercentUsed);

        var equipment = groups.Single(group => group.CostGroup == CostGroups.Equipment);
        Assert.False(equipment.HasSource);
        Assert.Equal(30_000m, equipment.Remaining);
        Assert.Equal(0.00m, equipment.PercentUsed);
    }

    [Fact]
    public void Without_an_approved_budget_the_overview_shows_cost_and_leaves_budget_figures_empty()
    {
        var (groups, totals) = ProjectOverview.Compute(null, ProjectCosts.ByGroup(Expenses, Sources));

        Assert.Null(totals.Budget);
        Assert.Null(totals.RemainingBudget);
        Assert.Null(totals.PercentUsed);
        Assert.Equal(244_500.50m, totals.Recognized);
        Assert.All(groups, group =>
        {
            Assert.Null(group.Budgeted);
            Assert.Null(group.Remaining);
            Assert.Null(group.PercentUsed);
        });
    }

    [Fact]
    public void Obligations_list_what_is_still_owed_to_each_creditor()
    {
        var result = Obligations.Compute(Expenses, ProjectCosts.ByGroup(Expenses, Sources));

        // Supplier 1: 45,000.50 custody (paid at approval) + 20,000 credit of which 5,000 is paid.
        var first = Assert.Single(result.Suppliers, balance => balance.SupplierId == 1);
        Assert.Equal(20_000m, first.CreditRecognized);
        Assert.Equal(45_000.50m, first.CustodyRecognized);
        Assert.Equal(50_000.50m, first.Paid);
        Assert.Equal(15_000m, first.Outstanding);
        Assert.Equal(1, first.OpenInvoices);

        // Supplier 2 has only a supply waiting for review: not a debt yet.
        Assert.DoesNotContain(result.Suppliers, balance => balance.SupplierId == 2);

        Assert.Equal(new OwedFigures(66_200.50m, 50_000.50m, 16_200m), result.SupplierTotals);
        Assert.Equal(new OwedFigures(3_300m, 3_300m, 0m), result.WithoutSupplier);

        Assert.Equal([CostGroups.Labor, CostGroups.Subcontractors, CostGroups.Equipment, CostGroups.Staff], result.Groups.Select(group => group.CostGroup));
        Assert.Equal(new OwedGroup(CostGroups.Labor, true, 55_000m, 40_000m, 15_000m), result.Groups[0]);
        Assert.Equal(new OwedGroup(CostGroups.Subcontractors, true, 120_000m, 100_000m, 20_000m), result.Groups[1]);
        Assert.Equal(new OwedGroup(CostGroups.Equipment, false, 0m, 0m, 0m), result.Groups[2]);
        Assert.Equal(new OwedGroup(CostGroups.Staff, false, 0m, 0m, 0m), result.Groups[3]);

        // The same outstanding as the overview: one rule.
        Assert.Equal(51_200m, result.TotalOutstanding);
    }

    [Theory]
    [InlineData(null, "100", null)]
    [InlineData("0", "100", null)]
    [InlineData("300", "100", "33.33")]
    [InlineData("200", "1", "0.50")]
    [InlineData("3", "2", "66.67")]
    public void Percent_used_is_rounded_to_two_decimals_and_never_divides_by_zero(string? budget, string recognized, string? expected)
    {
        var percent = ProjectOverview.PercentUsed(budget is null ? null : decimal.Parse(budget, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(recognized, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), percent);
    }

    private static CostGroupFigures Group(IReadOnlyList<CostGroupFigures> groups, string group) =>
        groups.Single(candidate => candidate.CostGroup == group);

    private static ExpenseSettlementRow Row(
        int id, ExpenseKind kind, ExpenseCategory? category, ExpenseStatus status, PaymentChannel channel, int? supplierId, decimal amount, decimal allocated) =>
        new(id, kind, status, channel, category, supplierId, ProjectItemId: null, amount, allocated);
}
