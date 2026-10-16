using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Employees;

/// <summary>One department, as every department endpoint returns it.</summary>
internal sealed record DepartmentView(int Id, string NameAr, string NameEn, bool IsActive, string RowVersion)
{
    public static DepartmentView From(Department department) =>
        new(department.Id, department.NameAr, department.NameEn, department.IsActive, RowVersions.Format(department.RowVersion));
}

/// <summary><c>GET /api/hr/departments</c>: every department, by English name (hr View).</summary>
internal static class ListDepartments
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/hr/departments", HandleAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListDepartments")
            .WithSummary("List the departments");

    internal static async Task<Ok<IReadOnlyList<DepartmentView>>> HandleAsync(HrDbContext db, CancellationToken cancellationToken)
    {
        var departments = await db.Departments.AsNoTracking()
            .OrderBy(department => department.NameEn)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<DepartmentView>>(departments.Select(DepartmentView.From).ToList());
    }
}

/// <summary><c>POST /api/hr/departments</c> (hr Create) and <c>PUT /api/hr/departments/{id}</c> (hr Edit): names must be new.</summary>
internal static class SaveDepartment
{
    internal sealed record Request(string? NameAr, string? NameEn, bool? IsActive, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/hr/departments", CreateAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Create)
            .WithName("CreateDepartment")
            .WithSummary("Add a department. 409 department_exists");

        api.MapPut("/hr/departments/{departmentId:int}", UpdateAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("UpdateDepartment")
            .WithSummary("Rename, deactivate or reactivate a department. 409 department_exists");
    }

    internal static async Task<Results<Created<DepartmentView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        Request request, HttpContext httpContext, HrDbContext db, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (nameAr, nameEn) = Names(request, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var department = new Department
        {
            NameAr = nameAr!,
            NameEn = nameEn!,
            IsActive = request.IsActive ?? true,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        db.Departments.Add(department);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (NameTaken(exception))
        {
            return HrProblems.DepartmentTaken();
        }

        log.DepartmentCreated(caller.UserId, department.Id);
        return TypedResults.Created($"/api/hr/departments/{department.Id}", DepartmentView.From(department));
    }

    internal static async Task<Results<Ok<DepartmentView>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        int departmentId, Request request, HttpContext httpContext, HrDbContext db, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (nameAr, nameEn) = Names(request, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var department = await db.Departments.SingleOrDefaultAsync(candidate => candidate.Id == departmentId, cancellationToken);
        if (department is null)
        {
            return HrProblems.DepartmentNotFound();
        }

        department.NameAr = nameAr!;
        department.NameEn = nameEn!;
        department.IsActive = request.IsActive ?? department.IsActive;
        department.UpdatedAt = clock.UtcNow;
        department.UpdatedBy = caller.UserId;
        db.Entry(department).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (NameTaken(exception))
        {
            return HrProblems.DepartmentTaken();
        }

        log.DepartmentUpdated(caller.UserId, department.Id);
        return TypedResults.Ok(DepartmentView.From(department));
    }

    private static (string? NameAr, string? NameEn) Names(Request request, ValidationErrors errors) =>
        (Input.RequiredText(request.NameAr, DepartmentConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name"),
         Input.RequiredText(request.NameEn, DepartmentConfiguration.NameMaxLength, errors, "nameEn", "The English name"));

    private static bool NameTaken(DbUpdateException exception) =>
        SqlViolations.IsUnique(exception, DepartmentConfiguration.NameArIndex)
        || SqlViolations.IsUnique(exception, DepartmentConfiguration.NameEnIndex);
}
