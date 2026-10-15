using System.Globalization;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Budgets;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Items;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Planning;

/// <summary>
/// Budget against actual, to the piastre, with real numbers: every figure of every group, the totals and the
/// per-item rows.
/// </summary>
public sealed class BudgetComparisonTests
{
    private static readonly BudgetLineAmount[] Lines =
    [
        new(1, CostGroups.Materials, ProjectItemId: 5, 60_000m),
        new(1, CostGroups.Materials, ProjectItemId: null, 40_000m),
        new(1, CostGroups.Labor, ProjectItemId: null, 50_000m),
        new(1, CostGroups.Subcontractors, ProjectItemId: 6, 200_000m),
        new(1, CostGroups.Equipment, ProjectItemId: null, 30_000m),
        new(1, CostGroups.SiteExpenses, ProjectItemId: null, 10_000m),
    ];

    /// <summary>The project's supplies and expenses: approved, waiting, returned and rejected, custody and credit.</summary>
    private static readonly ExpenseSettlementRow[] Expenses =
    [
        Supply(1, ExpenseStatus.Approved, PaymentChannel.Custody, 45_000.50m, allocated: 0),
        Supply(2, ExpenseStatus.Approved, PaymentChannel.Credit, 20_000m, allocated: 5_000m),
        Supply(3, ExpenseStatus.Recorded, PaymentChannel.Credit, 7_000m, allocated: 0),
        Supply(4, ExpenseStatus.Returned, PaymentChannel.Custody, 3_000m, allocated: 0),
        Expense(5, ExpenseCategory.Lab, ExpenseStatus.Approved, PaymentChannel.Custody, 2_500m, allocated: 0),
        Expense(6, ExpenseCategory.PettyCash, ExpenseStatus.Approved, PaymentChannel.Custody, 800m, allocated: 0),
        Expense(7, ExpenseCategory.Other, ExpenseStatus.Approved, PaymentChannel.Credit, 1_200m, allocated: 0),
        Expense(8, ExpenseCategory.Guards, ExpenseStatus.Rejected, PaymentChannel.Custody, 9_999m, allocated: 0),
    ];

    private static IEnumerable<CostGroupActuals> Actuals() =>
        BudgetComparison.FromExpenses(Expenses).Concat(
        [
            CostGroupActuals.From(CostGroups.Labor, new ProjectCostFigures(55_000m, 40_000m, 12, 3_000m, 2, 0)),
            CostGroupActuals.From(CostGroups.Subcontractors, new ProjectCostFigures(120_000m, 100_000m, 4, 0, 0, 80_000m)),
        ]);

    [Fact]
    public void Every_cost_group_is_compared_with_its_approved_cost_only()
    {
        var (groups, _) = BudgetComparison.ByGroup(Lines, Actuals());

        Assert.Equal(CostGroups.All, groups.Select(group => group.CostGroup));

        // Materials: approved supplies 45,000.50 + 20,000. The recorded supply waits apart; the returned one counts nowhere.
        var materials = groups.Single(group => group.CostGroup == CostGroups.Materials);
        Assert.Equal(100_000m, materials.Budgeted);
        Assert.Equal(65_000.50m, materials.Recognized);
        Assert.Equal(34_999.50m, materials.Variance);
        Assert.Equal(35.00m, materials.VariancePercent); // 34.9995 rounded half away from zero
        Assert.Equal(50_000.50m, materials.Paid);        // custody paid at approval + 5,000 allocated on credit
        Assert.Equal(15_000m, materials.Outstanding);
        Assert.Equal(7_000m, materials.PendingReview);
        Assert.Equal(0m, materials.Committed);

        // Site expenses: the lab expense; the rejected guards expense is never a cost.
        var site = groups.Single(group => group.CostGroup == CostGroups.SiteExpenses);
        Assert.Equal(10_000m, site.Budgeted);
        Assert.Equal(2_500m, site.Recognized);
        Assert.Equal(7_500m, site.Variance);
        Assert.Equal(75.00m, site.VariancePercent);
        Assert.Equal(2_500m, site.Paid);
        Assert.Equal(0m, site.Outstanding);

        // Other: petty cash and category Other. Nothing budgeted, so no percent.
        var other = groups.Single(group => group.CostGroup == CostGroups.Other);
        Assert.Equal(0m, other.Budgeted);
        Assert.Equal(2_000m, other.Recognized);
        Assert.Equal(-2_000m, other.Variance);
        Assert.Null(other.VariancePercent);
        Assert.Equal(800m, other.Paid);
        Assert.Equal(1_200m, other.Outstanding);

        // Labor is over budget: a negative variance.
        var labor = groups.Single(group => group.CostGroup == CostGroups.Labor);
        Assert.Equal(50_000m, labor.Budgeted);
        Assert.Equal(55_000m, labor.Recognized);
        Assert.Equal(-5_000m, labor.Variance);
        Assert.Equal(-10.00m, labor.VariancePercent);
        Assert.Equal(40_000m, labor.Paid);
        Assert.Equal(15_000m, labor.Outstanding);
        Assert.Equal(3_000m, labor.PendingReview);

        var subcontractors = groups.Single(group => group.CostGroup == CostGroups.Subcontractors);
        Assert.Equal(200_000m, subcontractors.Budgeted);
        Assert.Equal(120_000m, subcontractors.Recognized);
        Assert.Equal(80_000m, subcontractors.Variance);
        Assert.Equal(40.00m, subcontractors.VariancePercent);
        Assert.Equal(20_000m, subcontractors.Outstanding);
        Assert.Equal(80_000m, subcontractors.Committed);

        // No equipment source registered: the group is zero, and the whole budget is still unspent.
        var equipment = groups.Single(group => group.CostGroup == CostGroups.Equipment);
        Assert.Equal(30_000m, equipment.Budgeted);
        Assert.Equal(0m, equipment.Recognized);
        Assert.Equal(30_000m, equipment.Variance);
        Assert.Equal(100.00m, equipment.VariancePercent);
        Assert.Equal(0m, equipment.Paid);
    }

    [Fact]
    public void The_totals_are_the_sums_of_the_groups()
    {
        var (_, totals) = BudgetComparison.ByGroup(Lines, Actuals());

        Assert.Equal(390_000m, totals.Budgeted);
        Assert.Equal(244_500.50m, totals.Recognized);
        Assert.Equal(145_499.50m, totals.Variance);
        Assert.Equal(37.31m, totals.VariancePercent); // 37.3075…
        Assert.Equal(193_300.50m, totals.Paid);
        Assert.Equal(51_200m, totals.Outstanding);
        Assert.Equal(10_000m, totals.PendingReview);
        Assert.Equal(80_000m, totals.Committed);
    }

    [Fact]
    public void Two_sources_of_one_group_are_added_and_a_group_with_no_source_is_zero()
    {
        var (groups, totals) = BudgetComparison.ByGroup(
            [new BudgetLineAmount(1, CostGroups.Labor, null, 1_000m)],
            [
                CostGroupActuals.From(CostGroups.Labor, new ProjectCostFigures(300.25m, 100m, 1, 50m, 1, 0)),
                CostGroupActuals.From(CostGroups.Labor, new ProjectCostFigures(199.75m, 0m, 1, 0, 0, 0)),
            ]);

        var labor = groups.Single(group => group.CostGroup == CostGroups.Labor);
        Assert.Equal(500m, labor.Recognized);
        Assert.Equal(100m, labor.Paid);
        Assert.Equal(50m, labor.PendingReview);
        Assert.Equal(500m, labor.Variance);
        Assert.Equal(50.00m, labor.VariancePercent);
        Assert.All(groups.Where(group => group.CostGroup != CostGroups.Labor), group => Assert.Equal(0m, group.Recognized));
        Assert.Equal(500m, totals.Recognized);
    }

    [Fact]
    public void Without_a_budget_every_figure_is_still_shown_and_no_percent_is_invented()
    {
        var (groups, totals) = BudgetComparison.ByGroup([], Actuals());

        Assert.All(groups, group => Assert.Null(group.VariancePercent));
        Assert.Equal(0m, totals.Budgeted);
        Assert.Equal(-244_500.50m, totals.Variance);
        Assert.Null(totals.VariancePercent);
    }

    [Fact]
    public void Items_compare_their_budget_lines_with_their_recognized_cost_and_are_never_totalled()
    {
        IReadOnlyList<ItemTotalsRow> items =
        [
            new(5, "خرسانة", "Concrete", TotalCost: 70_000m, Paid: 50_000m, Outstanding: 20_000m),
            new(6, "بياض", "Plaster", TotalCost: 0m, Paid: 0m, Outstanding: 0m),
            new(7, "بلاط", "Tiling", TotalCost: 0m, Paid: 0m, Outstanding: 0m),
            new(8, "دهانات", "Painting", TotalCost: 1_000m, Paid: 0m, Outstanding: 1_000m),
        ];

        var rows = BudgetComparison.ByItem(Lines, items);

        // Tiling has no budget line and no cost: left out. The order is the items' order.
        Assert.Equal([5, 6, 8], rows.Select(row => row.ItemId));

        var concrete = rows[0];
        Assert.Equal(60_000m, concrete.Budgeted);
        Assert.Equal(70_000m, concrete.Recognized);
        Assert.Equal(-10_000m, concrete.Variance);
        Assert.Equal(-16.67m, concrete.VariancePercent);
        Assert.Equal(50_000m, concrete.Paid);
        Assert.Equal(20_000m, concrete.Outstanding);

        var plaster = rows[1];
        Assert.Equal(200_000m, plaster.Budgeted);
        Assert.Equal(0m, plaster.Recognized);
        Assert.Equal(200_000m, plaster.Variance);
        Assert.Equal(100.00m, plaster.VariancePercent);

        var painting = rows[2];
        Assert.Equal(0m, painting.Budgeted);
        Assert.Equal(-1_000m, painting.Variance);
        Assert.Null(painting.VariancePercent);
    }

    [Fact]
    public void Group_totals_list_every_group_even_with_no_line()
    {
        var totals = BudgetComparison.GroupTotals(Lines);

        Assert.Equal(CostGroups.All, totals.Select(total => total.CostGroup));
        Assert.Equal(
            [50_000m, 200_000m, 100_000m, 30_000m, 10_000m, 0m],
            totals.Select(total => total.Amount));
    }

    [Theory]
    [InlineData("Supply", null, CostGroups.Materials)]
    [InlineData("Expense", "Lab", CostGroups.SiteExpenses)]
    [InlineData("Expense", "Guards", CostGroups.SiteExpenses)]
    [InlineData("Expense", "PieceWork", CostGroups.SiteExpenses)]
    [InlineData("Expense", "Other", CostGroups.Other)]
    [InlineData("Expense", "PettyCash", CostGroups.Other)]
    public void Each_expense_record_lands_in_exactly_one_cost_group(string kind, string? category, string group)
    {
        // By name: the module's enums are internal, so a public test method can't take them as parameters.
        ExpenseCategory? parsedCategory = category is null ? null : Enum.Parse<ExpenseCategory>(category);

        Assert.Equal(group, BudgetComparison.GroupOf(Enum.Parse<ExpenseKind>(kind), parsedCategory));
    }

    [Theory]
    [InlineData("10.5", "118.75", "1246.88")]   // 1,246.875: the half goes away from zero
    [InlineData("3", "33.33", "99.99")]
    [InlineData("2.333", "0.15", "0.35")]       // 0.34995
    [InlineData("0.001", "0.01", "0.00")]
    public void A_priced_line_is_quantity_times_unit_price_to_the_piastre(string quantity, string unitPrice, string expected)
    {
        var amount = BudgetArithmetic.LineAmount(decimal.Parse(quantity, CultureInfo.InvariantCulture), decimal.Parse(unitPrice, CultureInfo.InvariantCulture));

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), amount);
    }

    private static ExpenseSettlementRow Supply(int id, ExpenseStatus status, PaymentChannel channel, decimal amount, decimal allocated) =>
        new(id, ExpenseKind.Supply, status, channel, null, SupplierId: 1, ProjectItemId: null, amount, allocated);

    private static ExpenseSettlementRow Expense(int id, ExpenseCategory category, ExpenseStatus status, PaymentChannel channel, decimal amount, decimal allocated) =>
        new(id, ExpenseKind.Expense, status, channel, category, SupplierId: null, ProjectItemId: null, amount, allocated);
}
