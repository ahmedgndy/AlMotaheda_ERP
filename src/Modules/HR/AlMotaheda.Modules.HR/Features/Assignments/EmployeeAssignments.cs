using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Assignments;

internal sealed record EmployeeAssignmentView(
    int Id, int EmployeeId, string EmployeeNo, string EmployeeNameAr, string EmployeeNameEn, int ProjectId, string? ProjectCode, string? ProjectNameAr, string? ProjectNameEn,
    string Role, DateOnly StartDate, DateOnly? EndDate, string? Notes, DateTime AssignedAt, int AssignedBy, DateTime? EndedAt, int? EndedBy, string? EndReason, string RowVersion)
{
    public static EmployeeAssignmentView From(EmployeeAssignment assignment, Employee employee, ProjectName? project) => new(
        assignment.Id, assignment.EmployeeId, employee.EmployeeNo, employee.NameAr, employee.NameEn, assignment.ProjectId, project?.Code, project?.NameAr, project?.NameEn,
        assignment.Role.ToString(), assignment.StartDate, assignment.EndDate, assignment.Notes, assignment.AssignedAt, assignment.AssignedBy, assignment.EndedAt, assignment.EndedBy,
        assignment.EndReason, RowVersions.Format(assignment.RowVersion));
}

/// <summary>
/// Salaried employees on projects over time: <c>GET /api/hr/employees/{id}/assignments</c> (hr View),
/// <c>POST /api/hr/employees/{id}/assignments</c> and <c>POST …/assignments/{aid}/end</c> (hr Edit), and
/// <c>GET /api/hr/assignments?projectId=&amp;on=</c> (hr View): who was on a project on a day, for investigations.
/// Ended rows are kept; an employee is on one project once at a time (409 <c>already_assigned</c>).
/// </summary>
internal static class EmployeeAssignments
{
    internal sealed record AssignRequest(int? ProjectId, string? Role, string? StartDate, string? Notes);

    internal sealed record EndRequest(string? EndDate, string? Reason, string? RowVersion);

    internal sealed record ListResponse(IReadOnlyList<EmployeeAssignmentView> Items);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/hr/employees/{employeeId:int}/assignments", ListAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListEmployeeAssignments")
            .WithSummary("An employee's project assignments over time (includeEnded)");

        api.MapPost("/hr/employees/{employeeId:int}/assignments", AssignAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("AssignEmployee")
            .WithSummary("Put an employee on a project from a date (hr Edit). 409 already_assigned, employee_ended");

        api.MapPost("/hr/employees/{employeeId:int}/assignments/{assignmentId:int}/end", EndAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("EndEmployeeAssignment")
            .WithSummary("End an employee's stay on a project (hr Edit); the row is kept as history");

        api.MapGet("/hr/assignments", OnDateAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListAssignmentsOnDate")
            .WithSummary("Who was assigned where on a day (projectId, on=yyyy-MM-dd; default today): for investigations");
    }

    internal static async Task<Results<Ok<ListResponse>, ProblemHttpResult>> ListAsync(
        int employeeId, HttpContext httpContext, HrDbContext db, IProjectDirectory projects, CancellationToken cancellationToken, bool includeEnded = false)
    {
        if (HrCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return HrProblems.EmployeeNotFound();
        }

        var rows = await db.EmployeeAssignments.AsNoTracking()
            .Where(assignment => assignment.EmployeeId == employeeId && (includeEnded || assignment.EndDate == null))
            .OrderByDescending(assignment => assignment.StartDate).ThenByDescending(assignment => assignment.Id)
            .ToListAsync(cancellationToken);
        var names = await projects.FindAsync(rows.Select(row => row.ProjectId).Distinct().ToList(), cancellationToken);
        return TypedResults.Ok(new ListResponse(rows.Select(row => EmployeeAssignmentView.From(row, employee, names.GetValueOrDefault(row.ProjectId))).ToList()));
    }

    internal static async Task<Results<Created<EmployeeAssignmentView>, ValidationProblem, ProblemHttpResult>> AssignAsync(
        int employeeId,
        AssignRequest request,
        HttpContext httpContext,
        HrDbContext db,
        IProjectDirectory projects,
        CompanyClock clock,
        HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.ProjectId is null or < 1)
        {
            errors.Add("projectId", "projectId is required.");
        }

        var role = Input.RequiredName<EmployeeAssignmentRole>(request.Role, errors, "role");
        var startDate = Input.OptionalDate(request.StartDate, errors, "startDate", "The start date") ?? clock.Today;
        if (startDate > clock.Today)
        {
            errors.Add("startDate", "The start date can't be in the future.");
        }

        var notes = Input.OptionalText(request.Notes, EmployeeAssignmentConfiguration.NotesMaxLength, errors, "notes", "Notes");
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return HrProblems.EmployeeNotFound();
        }

        if (employee.Status == EmployeeStatus.Ended)
        {
            return HrProblems.Ended();
        }

        var project = (await projects.FindAsync([request.ProjectId!.Value], cancellationToken)).GetValueOrDefault(request.ProjectId.Value);
        if (project is null)
        {
            errors.Add("projectId", "The project was not found.");
            return HrProblems.Validation(errors);
        }

        // Stays on the same project never overlap: a new one starts on or after the day the last one ended.
        var lastEnd = await db.EmployeeAssignments.AsNoTracking()
            .Where(assignment => assignment.EmployeeId == employeeId && assignment.ProjectId == project.Id && assignment.EndDate != null)
            .MaxAsync(assignment => (DateOnly?)assignment.EndDate, cancellationToken);
        if (lastEnd is { } end && startDate < end)
        {
            errors.Add("startDate", $"The employee was on this project until {end:yyyy-MM-dd}; the start date can't be before that.");
            return HrProblems.Validation(errors);
        }

        var assignment = new EmployeeAssignment
        {
            EmployeeId = employeeId,
            ProjectId = project.Id,
            Role = role!.Value,
            StartDate = startDate,
            Notes = notes,
            AssignedAt = clock.UtcNow,
            AssignedBy = caller.UserId,
        };
        db.EmployeeAssignments.Add(assignment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, EmployeeAssignmentConfiguration.OpenIndex))
        {
            return HrProblems.AssignedAlready();
        }

        log.EmployeeAssigned(caller.UserId, employeeId, project.Id, assignment.Id, assignment.Role.ToString());
        return TypedResults.Created($"/api/hr/employees/{employeeId}/assignments", EmployeeAssignmentView.From(assignment, employee, project));
    }

    internal static async Task<Results<Ok<EmployeeAssignmentView>, ValidationProblem, ProblemHttpResult>> EndAsync(
        int employeeId,
        int assignmentId,
        EndRequest request,
        HttpContext httpContext,
        HrDbContext db,
        IProjectDirectory projects,
        CompanyClock clock,
        HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var endDate = Input.OptionalDate(request.EndDate, errors, "endDate", "The end date") ?? clock.Today;
        if (endDate > clock.Today)
        {
            errors.Add("endDate", "The end date can't be in the future.");
        }

        var reason = Input.OptionalText(request.Reason, EmployeeAssignmentConfiguration.NotesMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);
        var assignment = await db.EmployeeAssignments.SingleOrDefaultAsync(candidate => candidate.Id == assignmentId && candidate.EmployeeId == employeeId, cancellationToken);
        if (employee is null || assignment is null)
        {
            return HrProblems.AssignmentNotFound();
        }

        if (assignment.EndDate is not null)
        {
            return HrProblems.AssignmentAlreadyEnded();
        }

        if (endDate < assignment.StartDate)
        {
            errors.Add("endDate", $"The end date can't be before the start date ({assignment.StartDate:yyyy-MM-dd}).");
            return HrProblems.Validation(errors);
        }

        assignment.EndDate = endDate;
        assignment.EndedAt = clock.UtcNow;
        assignment.EndedBy = caller.UserId;
        assignment.EndReason = reason;
        db.Entry(assignment).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.AssignmentEnded(caller.UserId, employeeId, assignment.Id);
        var project = (await projects.FindAsync([assignment.ProjectId], cancellationToken)).GetValueOrDefault(assignment.ProjectId);
        return TypedResults.Ok(EmployeeAssignmentView.From(assignment, employee, project));
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> OnDateAsync(
        HttpContext httpContext, HrDbContext db, IProjectDirectory projects, CompanyClock clock, CancellationToken cancellationToken, int? projectId = null, string? on = null)
    {
        if (HrCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var day = Input.OptionalDate(on, errors, "on", "The day") ?? clock.Today;
        if (projectId is < 1)
        {
            errors.Add("projectId", "projectId must be a project id.");
        }

        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var rows = await db.EmployeeAssignments.AsNoTracking()
            .Where(assignment => (projectId == null || assignment.ProjectId == projectId) && assignment.StartDate <= day && (assignment.EndDate == null || assignment.EndDate >= day))
            .Join(db.Employees, assignment => assignment.EmployeeId, employee => employee.Id, (assignment, employee) => new { assignment, employee })
            .OrderBy(row => row.assignment.ProjectId).ThenBy(row => row.employee.NameEn)
            .Take(500)
            .ToListAsync(cancellationToken);
        var names = await projects.FindAsync(rows.Select(row => row.assignment.ProjectId).Distinct().ToList(), cancellationToken);
        return TypedResults.Ok(new ListResponse(rows.Select(row => EmployeeAssignmentView.From(row.assignment, row.employee, names.GetValueOrDefault(row.assignment.ProjectId))).ToList()));
    }
}
