using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using AlMotaheda.Modules.HR.Features.Payroll;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.CostAllocation;

/// <summary>The allocation policy, as the screens show it. <c>workingDaysPerMonth</c> is <c>null</c> until set.</summary>
internal sealed record CostAllocationPolicyView(int? WorkingDaysPerMonth, DateTime? UpdatedAt, int? UpdatedBy, string RowVersion)
{
    public static CostAllocationPolicyView From(CostAllocationPolicy policy) =>
        new(policy.WorkingDaysPerMonth, policy.UpdatedAt, policy.UpdatedBy, RowVersions.Format(policy.RowVersion));
}

/// <summary>
/// <c>GET /api/hr/cost-allocation-policy</c> (hr View) and <c>PUT</c> (Executive only, re-checked in the database):
/// how many working days a month has when salaries are charged to projects.
/// </summary>
internal static class CostAllocationPolicyEndpoints
{
    internal sealed record Request(int? WorkingDaysPerMonth, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/hr/cost-allocation-policy", GetAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("GetCostAllocationPolicy")
            .WithSummary("Get the working days per month used to charge salaries to projects (null until set)");

        api.MapPut("/hr/cost-allocation-policy", PutAsync)
            .RequireRole(AccessRoles.Executive)
            .WithName("SetCostAllocationPolicy")
            .WithSummary("Set the working days per month used to charge salaries to projects (Executive only)");
    }

    internal static async Task<Ok<CostAllocationPolicyView>> GetAsync(HrDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(CostAllocationPolicyView.From(await db.CostAllocationPolicies.AsNoTracking().SingleAsync(cancellationToken)));

    internal static async Task<Results<Ok<CostAllocationPolicyView>, ValidationProblem, ProblemHttpResult>> PutAsync(
        Request request, HttpContext httpContext, HrDbContext db, IExecutiveCheck executives, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        if (request.WorkingDaysPerMonth is not (>= 1 and <= 31))
        {
            errors.Add("workingDaysPerMonth", "The working days per month must be between 1 and 31.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var userId = httpContext.User.GetUserId();
        var sessionId = httpContext.User.GetSessionId();
        if (userId is null || sessionId is null || !await executives.IsActiveExecutiveAsync(userId.Value, sessionId.Value, cancellationToken))
        {
            return ApiProblem.ForbiddenResult("Only an Executive can change the allocation policy.");
        }

        var policy = await db.CostAllocationPolicies.SingleAsync(cancellationToken);
        policy.WorkingDaysPerMonth = request.WorkingDaysPerMonth;
        policy.UpdatedAt = clock.UtcNow;
        policy.UpdatedBy = userId;
        db.Entry(policy).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.AllocationPolicyChanged(userId.Value);
        return TypedResults.Ok(CostAllocationPolicyView.From(policy));
    }
}

/// <summary>One salary allocation, as the screens show it. Money only with hr See money.</summary>
internal sealed record SalaryAllocationView(
    int Id,
    int WorkDaysId,
    int EmployeeId,
    string EmployeeNo,
    string EmployeeNameAr,
    string EmployeeNameEn,
    int ProjectId,
    int? ProjectItemId,
    string Month,
    decimal Days,
    int WorkingDays,
    decimal? MonthlyPay,
    decimal? Amount,
    string Status,
    bool SalaryPaid,
    DateTime CreatedAt,
    DateTime? ReversedAt,
    string? ReverseReason);

/// <summary>
/// <c>POST /api/hr/salary-allocations/run</c> <c>{ month }</c>: charge the month's approved project days to their projects
/// (hr Approve + See money: the Accountant). Safe to run again at any time: unchanged entries are skipped, corrected
/// ones are reversed and charged again, so a salary period is never charged twice.
/// </summary>
internal static class RunSalaryAllocation
{
    internal sealed record Request(string? Month);

    internal sealed record Response(
        string Month,
        int WorkingDays,
        int Created,
        int Reversed,
        int Unchanged,
        IReadOnlyList<SkippedEntry> Skipped,
        decimal? ChargedTotal);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/hr/salary-allocations/run", HandleAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Approve)
            .RequirePermission(HrPermissions.Module, PermissionAction.SeeMoney)
            .WithName("RunSalaryAllocation")
            .WithSummary("Charge a month's approved project days to the projects (idempotent). 409 allocation_policy_not_set");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        HrDbContext db,
        CompanyClock clock,
        HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var month = PayrollInputs.ParseMonth(request.Month, errors);
        if (month > PayrollMonths.FirstDay(clock.Today))
        {
            errors.Add("month", "A month that hasn't started can't be charged.");
        }

        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var policy = await db.CostAllocationPolicies.AsNoTracking().SingleAsync(cancellationToken);
        if (policy.WorkingDaysPerMonth is not { } workingDays)
        {
            return HrProblems.AllocationPolicyNotSet();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Allocation, cancellationToken))
        {
            return HrProblems.TryAgainLater();
        }

        var entries = await db.ProjectWorkDays.AsNoTracking()
            .Where(entry => entry.Month == month && (entry.Status == ProjectDaysStatus.Approved || entry.Status == ProjectDaysStatus.Superseded))
            .Select(entry => new PlanEntry(entry.Id, entry.EmployeeId, entry.ProjectId, entry.ProjectItemId, entry.Days, entry.Status))
            .ToListAsync(cancellationToken);
        var active = await db.SalaryCostAllocations
            .Where(allocation => allocation.Month == month && allocation.Status == AllocationStatus.Active)
            .ToListAsync(cancellationToken);

        var employeeIds = entries.Select(entry => entry.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(employee => employeeIds.Contains(employee.Id))
            .Select(employee => new SalaryCalculator.EmployeeFacts(
                employee.Id, employee.EmployeeNo, employee.NameAr, employee.NameEn, employee.DepartmentId, employee.HireDate, employee.EndDate))
            .ToListAsync(cancellationToken);
        var versions = await db.SalaryStructures.AsNoTracking().Where(version => employeeIds.Contains(version.EmployeeId))
            .Select(version => new SalaryCalculator.SalaryVersion(version.EmployeeId, version.EffectiveFrom, version.BasicSalary, version.FixedAllowance))
            .ToListAsync(cancellationToken);

        var plan = AllocationPlan.Build(
            month!.Value,
            workingDays,
            entries,
            active.Select(allocation => new PlanAllocation(allocation.Id, allocation.WorkDaysId, allocation.Days, allocation.MonthlyPay, allocation.WorkingDays, allocation.ProjectItemId)).ToList(),
            employees,
            versions);

        var now = clock.UtcNow;
        foreach (var (allocationId, reason) in plan.Reverse)
        {
            var allocation = active.Single(candidate => candidate.Id == allocationId);
            allocation.Status = AllocationStatus.Reversed;
            allocation.ReversedAt = now;
            allocation.ReversedBy = caller.UserId;
            allocation.ReverseReason = reason;
        }

        // Reversals are saved first, so a re-charge never meets the old row on the "one active allocation" index.
        await db.SaveChangesAsync(cancellationToken);

        db.SalaryCostAllocations.AddRange(plan.Create.Select(item => new SalaryCostAllocation
        {
            WorkDaysId = item.Entry.Id,
            EmployeeId = item.Entry.EmployeeId,
            ProjectId = item.Entry.ProjectId,
            ProjectItemId = item.Entry.ProjectItemId,
            Month = month.Value,
            Days = item.Entry.Days,
            MonthlyPay = item.MonthlyPay,
            WorkingDays = workingDays,
            Amount = item.Amount,
            Status = AllocationStatus.Active,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SalaryCostAllocationConfiguration.ActiveIndex))
        {
            return HrProblems.TryAgainLater();
        }

        await transaction.CommitAsync(cancellationToken);
        log.AllocationRun(caller.UserId, PayrollMonths.Format(month.Value), plan.Create.Count, plan.Reverse.Count, plan.Unchanged, plan.Skipped.Count);

        return TypedResults.Ok(new Response(
            PayrollMonths.Format(month.Value),
            workingDays,
            plan.Create.Count,
            plan.Reverse.Count,
            plan.Unchanged,
            plan.Skipped,
            caller.SeesMoney ? plan.Create.Sum(item => item.Amount) : null));
    }
}

/// <summary><c>GET /api/hr/salary-allocations</c>: the allocations, active and reversed (month; projectId; employeeId; status).</summary>
internal static class ListSalaryAllocations
{
    internal sealed record Response(IReadOnlyList<SalaryAllocationView> Items, int Page, int PageSize, int TotalCount, decimal? ActiveTotal);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/hr/salary-allocations", HandleAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListSalaryAllocations")
            .WithSummary("List salary allocations to projects (month yyyy-MM; projectId; employeeId; status Active or Reversed)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        HrDbContext db,
        CancellationToken cancellationToken,
        string? month = null,
        int? projectId = null,
        int? employeeId = null,
        string? status = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        DateOnly? monthFilter = month is null ? null : PayrollInputs.ParseMonth(month, errors);
        var statusFilter = Input.OptionalName<AllocationStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var query = db.SalaryCostAllocations.AsNoTracking();
        if (monthFilter is { } wantedMonth)
        {
            query = query.Where(allocation => allocation.Month == wantedMonth);
        }

        if (projectId is { } wantedProject)
        {
            query = query.Where(allocation => allocation.ProjectId == wantedProject);
        }

        if (employeeId is { } wantedEmployee)
        {
            query = query.Where(allocation => allocation.EmployeeId == wantedEmployee);
        }

        if (statusFilter is { } wantedStatus)
        {
            query = query.Where(allocation => allocation.Status == wantedStatus);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var activeTotal = await query.Where(allocation => allocation.Status == AllocationStatus.Active).SumAsync(allocation => (decimal?)allocation.Amount, cancellationToken) ?? 0;
        var seesMoney = caller.SeesMoney;
        var rows = await query
            .OrderByDescending(allocation => allocation.Month).ThenBy(allocation => allocation.EmployeeId).ThenBy(allocation => allocation.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(allocation => new
            {
                allocation,
                Employee = db.Employees.Where(employee => employee.Id == allocation.EmployeeId)
                    .Select(employee => new { employee.EmployeeNo, employee.NameAr, employee.NameEn }).First(),
                Paid = db.SalarySheets.Any(sheet => sheet.Month == allocation.Month && sheet.Status == SalarySheetStatus.Paid),
            })
            .ToListAsync(cancellationToken);

        var items = rows.Select(row => new SalaryAllocationView(
            row.allocation.Id, row.allocation.WorkDaysId, row.allocation.EmployeeId, row.Employee.EmployeeNo, row.Employee.NameAr, row.Employee.NameEn,
            row.allocation.ProjectId, row.allocation.ProjectItemId, PayrollMonths.Format(row.allocation.Month), row.allocation.Days, row.allocation.WorkingDays,
            seesMoney ? row.allocation.MonthlyPay : null, seesMoney ? row.allocation.Amount : null, row.allocation.Status.ToString(), row.Paid,
            row.allocation.CreatedAt, row.allocation.ReversedAt, row.allocation.ReverseReason)).ToList();

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount, seesMoney ? activeTotal : null));
    }
}
