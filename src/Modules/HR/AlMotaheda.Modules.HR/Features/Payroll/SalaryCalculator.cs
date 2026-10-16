using AlMotaheda.Modules.HR.Data;

namespace AlMotaheda.Modules.HR.Features.Payroll;

/// <summary>
/// The salary of each employee in a month, with no database access (docs/modernization/10-hr-design.md §3):
/// <c>net = basic + fixed allowance + bonuses − deductions − advance recoveries</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>On the payroll: hired on or before the month's last day and not ended before its first day.</item>
///   <item>The salary version used is the latest one starting on or before the month's first day. An employee hired
///   in the middle of the month uses the version starting on their hiring date.</item>
///   <item>No proration for a partial first or last month (Needs Confirmation).</item>
///   <item>A net below zero is refused, so HR fixes the inputs.</item>
/// </list>
/// </remarks>
internal static class SalaryCalculator
{
    internal sealed record EmployeeFacts(
        int Id, string EmployeeNo, string NameAr, string NameEn, int DepartmentId, DateOnly HireDate, DateOnly? EndDate);

    internal sealed record SalaryVersion(int EmployeeId, DateOnly EffectiveFrom, decimal BasicSalary, decimal FixedAllowance);

    internal sealed record InputAmount(int EmployeeId, PayrollInputKind Kind, decimal Amount);

    /// <summary>The lines, or the employees that block the calculation.</summary>
    internal sealed record Result(IReadOnlyList<SalarySheetLine> Lines, IReadOnlyList<string> MissingSalary, IReadOnlyList<string> NegativeNet)
    {
        public bool Succeeded => MissingSalary.Count == 0 && NegativeNet.Count == 0;
    }

    public static DateOnly LastDay(DateOnly month) => month.AddMonths(1).AddDays(-1);

    public static Result Calculate(
        DateOnly month,
        IEnumerable<EmployeeFacts> employees,
        IEnumerable<SalaryVersion> versions,
        IEnumerable<InputAmount> inputs)
    {
        var last = LastDay(month);
        var versionsByEmployee = versions.ToLookup(version => version.EmployeeId);
        var inputsByEmployee = inputs.ToLookup(input => input.EmployeeId);

        var lines = new List<SalarySheetLine>();
        var missing = new List<string>();
        var negative = new List<string>();

        foreach (var employee in employees
                     .Where(candidate => Employee.WorksIn(candidate.HireDate, candidate.EndDate, month, last))
                     .OrderBy(candidate => candidate.Id))
        {
            // The version in force on the first day the employee works this month.
            var from = employee.HireDate > month ? employee.HireDate : month;
            var version = versionsByEmployee[employee.Id]
                .Where(candidate => candidate.EffectiveFrom <= from)
                .MaxBy(candidate => candidate.EffectiveFrom);
            if (version is null)
            {
                missing.Add(employee.EmployeeNo);
                continue;
            }

            var amounts = inputsByEmployee[employee.Id].ToList();
            var bonuses = Sum(amounts, PayrollInputKind.Bonus);
            var deductions = Sum(amounts, PayrollInputKind.Deduction);
            var recoveries = Sum(amounts, PayrollInputKind.AdvanceRecovery);
            var net = version.BasicSalary + version.FixedAllowance + bonuses - deductions - recoveries;
            if (net < 0)
            {
                negative.Add(employee.EmployeeNo);
                continue;
            }

            lines.Add(new SalarySheetLine
            {
                EmployeeId = employee.Id,
                EmployeeNo = employee.EmployeeNo,
                NameAr = employee.NameAr,
                NameEn = employee.NameEn,
                DepartmentId = employee.DepartmentId,
                BasicSalary = version.BasicSalary,
                FixedAllowance = version.FixedAllowance,
                Bonuses = bonuses,
                Deductions = deductions,
                AdvanceRecoveries = recoveries,
                NetSalary = net,
            });
        }

        return new Result(lines, missing, negative);
    }

    private static decimal Sum(IEnumerable<InputAmount> inputs, PayrollInputKind kind) =>
        inputs.Where(input => input.Kind == kind).Sum(input => input.Amount);
}
