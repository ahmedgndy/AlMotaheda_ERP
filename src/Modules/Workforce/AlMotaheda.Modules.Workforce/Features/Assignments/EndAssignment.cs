using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Assignments;

/// <summary>
/// <c>POST /api/projects/{pid}/workers/{assignmentId}/end</c>: take a worker off a project. The row is kept with
/// its last day, so the history and every recorded day stay as they were.
/// </summary>
/// <remarks>
/// The last day can't be before the start, can't be in the future, and can't cut off days already recorded for
/// the worker on this project (those sheets would no longer pass the "assigned on that day" rule).
/// </remarks>
internal static class EndAssignment
{
    internal sealed record Request(string? EndDate, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/workers/{assignmentId:int}/end", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Edit)
            .WithName("EndWorkerAssignment")
            .WithSummary("End a worker's assignment to a project (the last day defaults to today; history is kept)");

    internal static async Task<Results<Ok<WorkerAssignmentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int assignmentId,
        Request request,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        WorkforceLog log,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var endDate = Input.OptionalDate(request.EndDate, errors, "endDate", "The end date");
        if (endDate > clock.Today)
        {
            errors.Add("endDate", "The end date can't be in the future.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return WorkforceProblems.CompletedProject();
        }

        var assignment = await db.WorkerAssignments
            .SingleOrDefaultAsync(candidate => candidate.Id == assignmentId && candidate.ProjectId == projectId, cancellationToken);
        if (assignment is null)
        {
            return WorkforceProblems.AssignmentNotFound();
        }

        if (assignment.EndDate is not null)
        {
            return WorkforceProblems.WrongStatus("This assignment has already ended.");
        }

        var last = endDate ?? clock.Today;
        if (last < assignment.StartDate)
        {
            errors.Add("endDate", "The end date can't be before the start date.");
            return WorkforceProblems.Validation(errors);
        }

        var recordedAfter = await db.AttendanceLines.AnyAsync(
            line => line.WorkerId == assignment.WorkerId && !line.IsVoid && line.WorkDate > last
                && line.Sheet.ProjectId == projectId,
            cancellationToken);
        if (recordedAfter)
        {
            return WorkforceProblems.WrongStatus("The worker is already recorded on this project after that date. Pick a later end date.");
        }

        assignment.EndDate = last;
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

        log.AssignmentEnded(caller.UserId, projectId, assignment.WorkerId, assignment.Id);

        var row = await AssignmentQueries.One(db, projectId, assignment.Id).SingleAsync(cancellationToken);
        return TypedResults.Ok(WorkerAssignmentView.From(row, caller.Can(PermissionAction.Edit), caller.SeesMoney));
    }
}
