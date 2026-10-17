using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Integration;

/// <summary>One active allocation with whether its month's salaries are paid.</summary>
internal sealed record StaffCostRow(
    int Id, int EmployeeId, string EmployeeNameAr, string EmployeeNameEn, int? ProjectItemId, DateOnly Month, decimal Days, decimal Amount, bool SalaryPaid);

internal static class StaffCostQueries
{
    /// <summary>A project's active salary allocations, to filter before <see cref="Rows"/> projects them.</summary>
    public static IQueryable<SalaryCostAllocation> Active(HrDbContext db, int projectId) =>
        db.SalaryCostAllocations.AsNoTracking().Where(allocation => allocation.ProjectId == projectId && allocation.Status == AllocationStatus.Active);

    /// <summary>
    /// The allocations as rows. Paid = the allocation once the month's salary sheet is Paid: the money left through
    /// payroll (company money in the ledger), so the project is never charged a second payment.
    /// </summary>
    public static IQueryable<StaffCostRow> Rows(HrDbContext db, IQueryable<SalaryCostAllocation> allocations) =>
        allocations
            .Select(allocation => new StaffCostRow(
                allocation.Id,
                allocation.EmployeeId,
                db.Employees.Where(employee => employee.Id == allocation.EmployeeId).Select(employee => employee.NameAr).First(),
                db.Employees.Where(employee => employee.Id == allocation.EmployeeId).Select(employee => employee.NameEn).First(),
                allocation.ProjectItemId,
                allocation.Month,
                allocation.Days,
                allocation.Amount,
                db.SalarySheets.Any(sheet => sheet.Month == allocation.Month && sheet.Status == SalarySheetStatus.Paid)));

    public static decimal Paid(StaffCostRow row) => row.SalaryPaid ? row.Amount : 0;
}

/// <summary>
/// HR's cost group of a project (<see cref="CostGroups.Staff"/>): engineers' and employees' salaries charged by
/// approved working days (docs/cost-flow §2.6). A month counts when it falls in the period asked.
/// </summary>
internal sealed class StaffCostSource(HrDbContext db) : IProjectCostSource
{
    public string Group => CostGroups.Staff;

    public async Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = StaffCostQueries.Active(db, projectId);
        if (from is { } start)
        {
            var firstMonth = new DateOnly(start.Year, start.Month, 1);
            query = query.Where(allocation => allocation.Month >= firstMonth);
        }

        if (to is { } end)
        {
            query = query.Where(allocation => allocation.Month <= end);
        }

        var rows = await StaffCostQueries.Rows(db, query).ToListAsync(cancellationToken);
        return new ProjectCostFigures(rows.Sum(row => row.Amount), rows.Sum(StaffCostQueries.Paid), rows.Count, 0, 0, 0);
    }
}

/// <summary>HR's answer to <see cref="IItemCostSource"/>: active salary allocations per project item.</summary>
internal sealed class StaffItemCosts(HrDbContext db) : IItemCostSource
{
    public string Group => CostGroups.Staff;

    public async Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken) =>
        (await StaffCostQueries.Rows(db, StaffCostQueries.Active(db, projectId)).ToListAsync(cancellationToken))
            .GroupBy(row => row.ProjectItemId)
            .Select(group => new ItemCostTotal(
                group.Key, group.Sum(row => row.Amount), group.Sum(StaffCostQueries.Paid), group.Count(), group.Select(row => row.EmployeeId).Distinct().Count()))
            .ToList();

    public async Task<IReadOnlyList<ItemSourceLine>> LinesAsync(int projectId, int? itemId, CancellationToken cancellationToken) =>
        (await StaffCostQueries.Rows(db, StaffCostQueries.Active(db, projectId).Where(allocation => allocation.ProjectItemId == itemId)).ToListAsync(cancellationToken))
            .OrderBy(row => row.Month)
            .ThenBy(row => row.EmployeeNameEn)
            .Select(row =>
            {
                var paid = StaffCostQueries.Paid(row);
                return new ItemSourceLine(
                    "SalaryAllocation", row.Id, row.Month, "Salary", $"{row.Days} days in {row.Month:yyyy-MM}", row.EmployeeId, row.EmployeeNameAr,
                    row.EmployeeNameEn, row.Days, "Day", row.Amount, paid, row.Amount - paid, PaymentStates.Of(row.Amount, paid));
            })
            .ToList();

    public Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken) =>
        db.ProjectWorkDays.AsNoTracking().AnyAsync(entry => entry.ProjectId == projectId && entry.ProjectItemId == itemId, cancellationToken);
}
