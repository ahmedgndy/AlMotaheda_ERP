using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Features.CostAllocation;
using static AlMotaheda.Modules.HR.Features.Payroll.SalaryCalculator;

namespace AlMotaheda.Modules.HR.Tests.Unit;

/// <summary>Charging monthly salaries to projects by approved days (docs/cost-flow §2.6), with no database.</summary>
public sealed class AllocationPlanTests
{
    private static readonly DateOnly September = new(2026, 9, 1);

    private static EmployeeFacts Employee(int id, DateOnly? hired = null, DateOnly? ended = null) =>
        new(id, $"EMP-{id:000000}", "مهندس", $"Engineer {id}", 1, hired ?? new DateOnly(2024, 1, 1), ended);

    private static SalaryVersion Salary(int employeeId, decimal basic, string from = "2024-01-01", decimal allowance = 0m) =>
        new(employeeId, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), basic, allowance);

    private static PlanEntry Entry(int id, decimal days, ProjectDaysStatus status = ProjectDaysStatus.Approved, int employeeId = 1, int? itemId = null) =>
        new(id, employeeId, 9, itemId, days, status);

    [Fact]
    public void Twenty_thousand_over_twenty_working_days_for_five_days_is_five_thousand() =>
        Assert.Equal(5_000m, AllocationPlan.Amount(20_000m, 20, 5m));

    [Fact]
    public void A_new_approved_entry_is_charged_with_basic_plus_fixed_allowance()
    {
        var plan = AllocationPlan.Build(September, 20, [Entry(1, 5m)], [], [Employee(1)], [Salary(1, 18_000m, allowance: 2_000m)]);

        var created = Assert.Single(plan.Create);
        Assert.Equal((20_000m, 5_000m), (created.MonthlyPay, created.Amount));
        Assert.Empty(plan.Reverse);
    }

    [Fact]
    public void Running_again_with_nothing_changed_charges_nothing()
    {
        var plan = AllocationPlan.Build(September, 20, [Entry(1, 5m)], [new PlanAllocation(70, 1, 5m, 20_000m, 20, null)], [Employee(1)], [Salary(1, 20_000m)]);

        Assert.Equal((0, 0, 1), (plan.Create.Count, plan.Reverse.Count, plan.Unchanged));
    }

    [Fact]
    public void A_correction_reverses_the_old_charge_and_charges_the_new_days()
    {
        var plan = AllocationPlan.Build(
            September, 20,
            [Entry(1, 5m, ProjectDaysStatus.Superseded), Entry(2, 6m)],
            [new PlanAllocation(70, 1, 5m, 20_000m, 20, null)],
            [Employee(1)], [Salary(1, 20_000m)]);

        Assert.Equal(70, Assert.Single(plan.Reverse).AllocationId);
        Assert.Equal((2, 6_000m), (Assert.Single(plan.Create).Entry.Id, plan.Create[0].Amount));
    }

    [Fact]
    public void A_salary_revision_or_a_new_policy_charges_again_at_the_new_figure()
    {
        var revised = AllocationPlan.Build(September, 20, [Entry(1, 5m)], [new PlanAllocation(70, 1, 5m, 20_000m, 20, null)], [Employee(1)],
            [Salary(1, 20_000m), Salary(1, 24_000m, "2026-09-01")]);
        Assert.Equal((70, 6_000m), (Assert.Single(revised.Reverse).AllocationId, Assert.Single(revised.Create).Amount));

        var newPolicy = AllocationPlan.Build(September, 25, [Entry(1, 5m)], [new PlanAllocation(70, 1, 5m, 20_000m, 20, null)], [Employee(1)], [Salary(1, 20_000m)]);
        Assert.Equal(4_000m, Assert.Single(newPolicy.Create).Amount);

        // A version starting after the month's first day is next month's.
        var later = AllocationPlan.Build(September, 20, [Entry(1, 5m)], [], [Employee(1)], [Salary(1, 20_000m), Salary(1, 30_000m, "2026-09-15")]);
        Assert.Equal(5_000m, Assert.Single(later.Create).Amount);
    }

    [Fact]
    public void Missing_salaries_and_employees_not_working_in_the_month_are_skipped_with_a_reason()
    {
        var plan = AllocationPlan.Build(
            September, 20,
            [Entry(1, 5m), Entry(2, 5m, employeeId: 2), Entry(3, 5m, employeeId: 3)],
            [],
            [Employee(1), Employee(2, ended: new DateOnly(2026, 8, 31)), Employee(3)],
            [Salary(1, 20_000m), Salary(2, 20_000m)]);

        Assert.Single(plan.Create);
        Assert.Equal(
            [(2, AllocationPlan.NotWorking), (3, AllocationPlan.MissingSalary)],
            plan.Skipped.Select(skipped => (skipped.WorkDaysId, skipped.Reason)).Order());
    }

    [Fact]
    public void A_partial_month_uses_the_version_in_force_on_the_hiring_date_and_the_days_worked()
    {
        var plan = AllocationPlan.Build(September, 20, [Entry(1, 2.5m)], [], [Employee(1, hired: new DateOnly(2026, 9, 20))], [Salary(1, 16_000m, "2026-09-20")]);
        Assert.Equal(2_000m, Assert.Single(plan.Create).Amount);
    }
}
