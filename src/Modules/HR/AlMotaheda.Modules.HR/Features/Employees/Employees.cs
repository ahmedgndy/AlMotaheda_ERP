using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Employees;

/// <summary><c>GET /api/hr/employees</c>: one page of employees (hr View).</summary>
internal static class ListEmployees
{
    internal sealed record Response(IReadOnlyList<EmployeeView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/hr/employees", HandleAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListEmployees")
            .WithSummary("List the employees (search: names, number, national id or phone; departmentId; kind; status)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        HrDbContext db,
        CancellationToken cancellationToken,
        string? search = null,
        int? departmentId = null,
        string? kind = null,
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
        var kindFilter = Input.OptionalName<EmployeeKind>(kind, errors, "kind");
        var statusFilter = Input.OptionalName<EmployeeStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var query = EmployeeQueries.Search(db, Input.Clean(search), departmentId, kindFilter, statusFilter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(
            rows.Select(row => EmployeeView.From(row.Employee, row.Department, caller.SeesMoney)).ToList(), page, pageSize, totalCount));
    }
}

/// <summary><c>GET /api/hr/employees/{id}</c>: one employee (hr View).</summary>
internal static class GetEmployee
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/hr/employees/{employeeId:int}", HandleAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("GetEmployee")
            .WithSummary("Get one employee");

    internal static async Task<Results<Ok<EmployeeView>, ProblemHttpResult>> HandleAsync(
        int employeeId, HttpContext httpContext, HrDbContext db, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var view = await EmployeeView.LoadAsync(db, employeeId, caller.SeesMoney, cancellationToken);
        return view is null ? HrProblems.EmployeeNotFound() : TypedResults.Ok(view);
    }
}

/// <summary>
/// <c>POST /api/hr/employees</c> (hr Create) and <c>PUT /api/hr/employees/{id}</c> (hr Edit). The national id and the
/// linked user account must be new; the department must be active (on edit: when it changes). An ended employee
/// can't be edited.
/// </summary>
internal static class SaveEmployee
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? NationalId,
        string? Phone,
        string? Email,
        string? JobTitle,
        int? DepartmentId,
        string? Kind,
        string? HireDate,
        int? UserId,
        string? BankName,
        string? Iban,
        string? Notes,
        string? RowVersion) : IEmployeeFieldsRequest;

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/hr/employees", CreateAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Create)
            .WithName("CreateEmployee")
            .WithSummary("Add an employee. 409 national_id_exists, user_already_linked, department_inactive");

        api.MapPut("/hr/employees/{employeeId:int}", UpdateAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("UpdateEmployee")
            .WithSummary("Edit an employee. 409 national_id_exists, user_already_linked, department_inactive, employee_ended");
    }

    internal static async Task<Results<Created<EmployeeView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Request request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = EmployeeFields.Validate(request, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        if (await CheckLinksAsync(db, users, fields!, departmentChanged: true, userChanged: fields!.UserId is not null, cancellationToken) is { } problem)
        {
            return problem;
        }

        var employee = new Employee { CreatedAt = clock.UtcNow, CreatedBy = caller.UserId };
        fields.ApplyTo(employee, withBankDetails: caller.SeesMoney);
        db.Employees.Add(employee);

        if (await SaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        log.EmployeeCreated(caller.UserId, employee.Id);
        var view = await EmployeeView.LoadAsync(db, employee.Id, caller.SeesMoney, cancellationToken);
        return TypedResults.Created($"/api/hr/employees/{employee.Id}", view!);
    }

    internal static async Task<Results<Ok<EmployeeView>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        int employeeId, Request request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = EmployeeFields.Validate(request, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var employee = await db.Employees.SingleOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return HrProblems.EmployeeNotFound();
        }

        if (employee.Status == EmployeeStatus.Ended)
        {
            return HrProblems.Ended();
        }

        var departmentChanged = fields!.DepartmentId != employee.DepartmentId;
        var userChanged = fields.UserId is not null && fields.UserId != employee.UserId;
        if (await CheckLinksAsync(db, users, fields, departmentChanged, userChanged, cancellationToken) is { } problem)
        {
            return problem;
        }

        fields.ApplyTo(employee, withBankDetails: caller.SeesMoney);
        employee.UpdatedAt = clock.UtcNow;
        employee.UpdatedBy = caller.UserId;
        db.Entry(employee).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        if (await SaveAsync(db, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        log.EmployeeUpdated(caller.UserId, employee.Id);
        var view = await EmployeeView.LoadAsync(db, employee.Id, caller.SeesMoney, cancellationToken);
        return TypedResults.Ok(view!);
    }

    private static async Task<ProblemHttpResult?> CheckLinksAsync(
        HrDbContext db, IUserDirectory users, EmployeeFields fields, bool departmentChanged, bool userChanged, CancellationToken cancellationToken)
    {
        if (departmentChanged)
        {
            var department = await db.Departments.AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == fields.DepartmentId, cancellationToken);
            if (department is null)
            {
                return HrProblems.DepartmentNotFound();
            }

            if (!department.IsActive)
            {
                return HrProblems.InactiveDepartment();
            }
        }

        if (userChanged && !(await users.FindAsync([fields.UserId!.Value], cancellationToken)).ContainsKey(fields.UserId.Value))
        {
            return HrProblems.UserUnknown();
        }

        return null;
    }

    /// <summary>Saves; the unique indexes answer races and duplicates.</summary>
    private static async Task<ProblemHttpResult?> SaveAsync(HrDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, EmployeeConfiguration.NationalIdIndex))
        {
            return HrProblems.NationalIdExists();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, EmployeeConfiguration.UserIndex))
        {
            return HrProblems.UserLinked();
        }
    }
}

/// <summary>
/// <c>POST /api/hr/employees/{id}/end</c> (hr Edit): the employee leaves on <c>endDate</c> (their last working day,
/// on or after the hiring date). They stay on the payroll of the months they worked in.
/// </summary>
internal static class EndEmployee
{
    internal sealed record Request(string? EndDate, string? Reason, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/hr/employees/{employeeId:int}/end", HandleAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("EndEmployee")
            .WithSummary("End an employee's service. 409 employee_ended");

    internal static async Task<Results<Ok<EmployeeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int employeeId, Request request, HttpContext httpContext, HrDbContext db, CompanyClock clock, HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var endDate = Input.RequiredDate(request.EndDate, errors, "endDate", "The end date");
        var reason = Input.RequiredText(request.Reason, EmployeeConfiguration.ReasonMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var employee = await db.Employees.SingleOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return HrProblems.EmployeeNotFound();
        }

        if (employee.Status == EmployeeStatus.Ended)
        {
            return HrProblems.Ended();
        }

        if (endDate < employee.HireDate)
        {
            errors.Add("endDate", "The end date can't be before the hiring date.");
            return HrProblems.Validation(errors);
        }

        employee.Status = EmployeeStatus.Ended;
        employee.EndDate = endDate;
        employee.EndReason = reason;
        employee.UpdatedAt = clock.UtcNow;
        employee.UpdatedBy = caller.UserId;
        db.Entry(employee).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.EmployeeEnded(caller.UserId, employee.Id);
        var view = await EmployeeView.LoadAsync(db, employee.Id, caller.SeesMoney, cancellationToken);
        return TypedResults.Ok(view!);
    }
}
