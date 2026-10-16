using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Features.Payroll;
using static AlMotaheda.Modules.HR.Features.Payroll.SalaryCalculator;

namespace AlMotaheda.Modules.HR.Tests.Unit;

/// <summary>The salary of a month (docs/modernization/10-hr-design.md §3), with no database.</summary>
public sealed class SalaryCalculatorTests
{
    private static readonly DateOnly October = new(2026, 10, 1);

    private static EmployeeFacts Employee(int id, DateOnly hired, DateOnly? ended = null) =>
        new(id, $"EMP-{id:000000}", "موظف", $"Employee {id}", 1, hired, ended);

    [Fact]
    public void Net_is_basic_plus_allowance_plus_bonuses_minus_deductions_and_recoveries()
    {
        var result = Calculate(
            October,
            [Employee(1, new DateOnly(2025, 1, 1))],
            [new SalaryVersion(1, new DateOnly(2025, 1, 1), 10_000m, 1_500.50m)],
            [
                new InputAmount(1, PayrollInputKind.Bonus, 1_000m),
                new InputAmount(1, PayrollInputKind.Bonus, 250m),
                new InputAmount(1, PayrollInputKind.Deduction, 300.25m),
                new InputAmount(1, PayrollInputKind.AdvanceRecovery, 2_000m),
            ]);

        var line = Assert.Single(result.Lines);
        Assert.True(result.Succeeded);
        Assert.Equal((10_000m, 1_500.50m, 1_250m, 300.25m, 2_000m), (line.BasicSalary, line.FixedAllowance, line.Bonuses, line.Deductions, line.AdvanceRecoveries));
        Assert.Equal(10_450.25m, line.NetSalary);
    }

    [Fact]
    public void The_month_uses_the_version_in_force_on_its_first_day()
    {
        var versions = new[]
        {
            new SalaryVersion(1, new DateOnly(2025, 1, 1), 8_000m, 0m),
            new SalaryVersion(1, new DateOnly(2026, 10, 1), 9_000m, 0m),
            new SalaryVersion(1, new DateOnly(2026, 10, 15), 9_999m, 0m), // a raise in the middle of the month waits for November
        };

        var line = Assert.Single(Calculate(October, [Employee(1, new DateOnly(2025, 1, 1))], versions, []).Lines);

        Assert.Equal(9_000m, line.NetSalary);
    }

    [Fact]
    public void Someone_hired_in_the_month_uses_the_version_starting_on_their_hiring_date_without_proration()
    {
        var hired = new DateOnly(2026, 10, 20);

        var line = Assert.Single(Calculate(October, [Employee(1, hired)], [new SalaryVersion(1, hired, 6_000m, 500m)], []).Lines);

        Assert.Equal(6_500m, line.NetSalary);
    }

    [Fact]
    public void Only_employees_working_in_the_month_are_on_it()
    {
        var employees = new[]
        {
            Employee(1, new DateOnly(2025, 1, 1)),
            Employee(2, new DateOnly(2026, 11, 1)),                               // starts next month
            Employee(3, new DateOnly(2025, 1, 1), ended: new DateOnly(2026, 9, 30)), // left last month
            Employee(4, new DateOnly(2025, 1, 1), ended: new DateOnly(2026, 10, 1)), // last day is the 1st: still on it
        };
        var versions = employees.Select(employee => new SalaryVersion(employee.Id, employee.HireDate, 1_000m, 0m));

        var result = Calculate(October, employees, versions, []);

        Assert.Equal([1, 4], result.Lines.Select(line => line.EmployeeId));
    }

    [Fact]
    public void Missing_salaries_and_negative_nets_block_the_sheet_and_name_the_employees()
    {
        var result = Calculate(
            October,
            [Employee(1, new DateOnly(2025, 1, 1)), Employee(2, new DateOnly(2025, 1, 1)), Employee(3, new DateOnly(2025, 1, 1))],
            [new SalaryVersion(2, new DateOnly(2025, 1, 1), 1_000m, 0m), new SalaryVersion(3, new DateOnly(2025, 1, 1), 1_000m, 0m)],
            [new InputAmount(3, PayrollInputKind.Deduction, 1_000.01m)]);

        Assert.False(result.Succeeded);
        Assert.Equal(["EMP-000001"], result.MissingSalary);
        Assert.Equal(["EMP-000003"], result.NegativeNet);
    }

    [Fact]
    public void A_sheet_moves_only_along_its_flow()
    {
        var now = DateTime.UtcNow;
        var sheet = new SalarySheet { Month = October };
        sheet.Calculate([new SalarySheetLine { EmployeeId = 1, NetSalary = 100m }, new SalarySheetLine { EmployeeId = 2, NetSalary = 50.5m }], 7, now);
        Assert.Equal((150.5m, 2), (sheet.Total, sheet.EmployeeCount));

        Assert.Throws<InvalidOperationException>(() => sheet.Review(approve: true, 8, now, null));
        sheet.Submit(7, now);
        Assert.Throws<InvalidOperationException>(() => sheet.Calculate([], 7, now));

        sheet.Review(approve: false, 8, now, "Bonus missing");
        Assert.Equal(SalarySheetStatus.Returned, sheet.Status);
        sheet.Submit(7, now);
        sheet.Review(approve: true, 8, now, null);
        Assert.Throws<InvalidOperationException>(() => sheet.Submit(7, now));

        sheet.MarkPaid(12, new DateOnly(2026, 10, 31), 8, now);
        Assert.Equal((SalarySheetStatus.Paid, 12), (sheet.Status, sheet.PaymentRequestId));
    }
}
