using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Assignments;

/// <summary>
/// <c>POST /api/projects/{pid}/equipment/{assignmentId}/end</c>: the machine leaves the project. The stay gets its
/// end date and stays in the history; the machine is then free for another project. Its costs on this project stay
/// on this project.
/// </summary>
internal static class EndEquipmentAssignment
{
    internal sealed record Request(string? EndDate, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment/{assignmentId:int}/end", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.Edit)
            .WithName("EndEquipmentAssignment")
            .WithSummary("End a machine's stay on a project (endDate defaults to today); the stay is kept as history");

    internal static async Task<Results<Ok<EquipmentAssignmentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int assignmentId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var today = clock.Today;
        var errors = new ValidationErrors();
        var endDate = Input.OptionalDate(request.EndDate, errors, "endDate", "The end date") ?? today;
        if (endDate > today)
        {
            errors.Add("endDate", "The end date can't be in the future.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return FleetProblems.CompletedProject();
        }

        var assignment = await db.EquipmentAssignments
            .SingleOrDefaultAsync(candidate => candidate.Id == assignmentId && candidate.ProjectId == projectId, cancellationToken);
        if (assignment is null)
        {
            return FleetProblems.AssignmentNotFound();
        }

        if (assignment.EndDate is not null)
        {
            return FleetProblems.WrongStatus("This assignment has already ended.");
        }

        if (!AssignmentRules.CanEndOn(assignment.StartDate, endDate, today))
        {
            errors.Add("endDate", $"The end date can't be before the start date ({assignment.StartDate:yyyy-MM-dd}).");
            return FleetProblems.Validation(errors);
        }

        assignment.EndDate = endDate;
        assignment.EndedAt = clock.UtcNow;
        assignment.EndedBy = caller.UserId;
        db.Entry(assignment).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.AssignmentEnded(caller.UserId, projectId, assignment.EquipmentId, assignment.Id);

        var row = await AssignmentQueries.One(db, projectId, assignment.Id).SingleAsync(cancellationToken);
        var view = (await EquipmentAssignmentView.FromAsync([row], project, users, cancellationToken))[0];
        return TypedResults.Ok(view);
    }
}
