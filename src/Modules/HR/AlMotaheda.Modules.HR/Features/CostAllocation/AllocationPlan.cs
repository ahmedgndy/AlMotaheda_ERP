using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Features.Payroll;

namespace AlMotaheda.Modules.HR.Features.CostAllocation;

/// <summary>One approved (or superseded) entry, as the plan sees it.</summary>
internal sealed record PlanEntry(int Id, int EmployeeId, int ProjectId, int? ProjectItemId, decimal Days, ProjectDaysStatus Status);

/// <summary>One allocation that is active today.</summary>
internal sealed record PlanAllocation(int Id, int WorkDaysId, decimal Days, decimal MonthlyPay, int WorkingDays, int? ProjectItemId);

/// <summary>An allocation to write.</summary>
internal sealed record NewAllocation(PlanEntry Entry, decimal MonthlyPay, decimal Amount);

/// <summary>An entry the run could not charge, and why.</summary>
internal sealed record SkippedEntry(int WorkDaysId, int EmployeeId, string Reason);

/// <summary>What one run does: reverse some allocations, write others, leave the rest.</summary>
internal sealed record AllocationPlanResult(
    IReadOnlyList<(int AllocationId, string Reason)> Reverse,
    IReadOnlyList<NewAllocation> Create,
    int Unchanged,
    IReadOnlyList<SkippedEntry> Skipped);

/// <summary>
/// The salary allocation rule (docs/cost-flow §2.6), with no database access. A project's share of a month's
/// salary is (basic + fixed allowance in force on the month's first working day) ÷ the policy's working days × the
/// approved days, to the piastre. The run is idempotent: an entry already charged with the same days, pay, working
/// days and item is left alone; anything else is reversed and charged again, so nothing is ever charged twice.
/// </summary>
internal static class AllocationPlan
{
    public const string MissingSalary = "missing_salary";
    public const string NotWorking = "not_working_in_month";

    public static decimal Amount(decimal monthlyPay, int workingDays, decimal days) =>
        Math.Round(monthlyPay / workingDays * days, 2, MidpointRounding.AwayFromZero);

    /// <param name="employees">Every employee named by an entry.</param>
    /// <param name="versions">Their salary versions.</param>
    public static AllocationPlanResult Build(
        DateOnly month,
        int workingDays,
        IReadOnlyList<PlanEntry> entries,
        IReadOnlyList<PlanAllocation> active,
        IReadOnlyList<SalaryCalculator.EmployeeFacts> employees,
        IReadOnlyList<SalaryCalculator.SalaryVersion> versions)
    {
        var last = SalaryCalculator.LastDay(month);
        var activeByEntry = active.ToDictionary(allocation => allocation.WorkDaysId);
        var employeesById = employees.ToDictionary(employee => employee.Id);
        var versionsByEmployee = versions.ToLookup(version => version.EmployeeId);

        var reverse = new List<(int, string)>();
        var create = new List<NewAllocation>();
        var skipped = new List<SkippedEntry>();
        var unchanged = 0;

        // Charged entries that are no longer approved (a correction replaced them) are reversed.
        var approvedIds = entries.Where(entry => entry.Status == ProjectDaysStatus.Approved).Select(entry => entry.Id).ToHashSet();
        foreach (var allocation in active.Where(allocation => !approvedIds.Contains(allocation.WorkDaysId)))
        {
            reverse.Add((allocation.Id, "The days were corrected; the correction is charged instead."));
        }

        foreach (var entry in entries.Where(entry => entry.Status == ProjectDaysStatus.Approved).OrderBy(entry => entry.Id))
        {
            if (!employeesById.TryGetValue(entry.EmployeeId, out var employee) || !Employee.WorksIn(employee.HireDate, employee.EndDate, month, last))
            {
                skipped.Add(new SkippedEntry(entry.Id, entry.EmployeeId, NotWorking));
                continue;
            }

            // The same version rule as the salary sheet: in force on the first day the employee works this month.
            var from = employee.HireDate > month ? employee.HireDate : month;
            var version = versionsByEmployee[entry.EmployeeId].Where(candidate => candidate.EffectiveFrom <= from).MaxBy(candidate => candidate.EffectiveFrom);
            if (version is null)
            {
                skipped.Add(new SkippedEntry(entry.Id, entry.EmployeeId, MissingSalary));
                continue;
            }

            var monthlyPay = version.BasicSalary + version.FixedAllowance;
            if (activeByEntry.TryGetValue(entry.Id, out var current))
            {
                if (current.Days == entry.Days && current.MonthlyPay == monthlyPay && current.WorkingDays == workingDays && current.ProjectItemId == entry.ProjectItemId)
                {
                    unchanged++;
                    continue;
                }

                reverse.Add((current.Id, "The salary, the working days or the project item changed; charged again."));
            }

            create.Add(new NewAllocation(entry, monthlyPay, Amount(monthlyPay, workingDays, entry.Days)));
        }

        return new AllocationPlanResult(reverse, create, unchanged, skipped);
    }
}
