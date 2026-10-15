using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Assignments;

/// <summary>
/// <c>POST /api/projects/{pid}/workers</c>: put an existing worker on a project, optionally with a rate for this
/// project only. The worker row is reused; nothing about the worker is copied.
/// </summary>
/// <remarks>
/// A worker can't be on the same project twice at once: 409 <c>already_assigned</c> when an open assignment
/// exists, or when an ended one still covers the new start date (two rates for one day would be ambiguous).
/// </remarks>
internal static class AssignWorker
{
    internal sealed record Request(int? WorkerId, decimal? DailyRate, string? StartDate);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/workers", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Edit)
            .WithName("AssignWorkerToProject")
            .WithSummary("Assign a worker to a project (optional project rate; start date defaults to today)");

    internal static async Task<Results<Created<WorkerAssignmentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
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
        if (request.WorkerId is null or < 1)
        {
            errors.Add("workerId", "workerId is required.");
        }

        var rate = NumberInput.OptionalMoney(request.DailyRate, errors, "dailyRate", "The daily rate");
        var startDate = Input.OptionalDate(request.StartDate, errors, "startDate", "The start date");
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

        var workerId = request.WorkerId!.Value;
        var worker = await db.Workers.AsNoTracking()
            .Where(candidate => candidate.Id == workerId)
            .Select(candidate => new { candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (worker is null)
        {
            errors.Add("workerId", "The worker was not found.");
            return WorkforceProblems.Validation(errors);
        }

        if (worker.Status != WorkerStatus.Active)
        {
            return WorkforceProblems.InactiveWorker(workerId);
        }

        var start = startDate ?? clock.Today;
        var overlaps = await db.WorkerAssignments.AnyAsync(
            assignment => assignment.WorkerId == workerId && assignment.ProjectId == projectId
                && (assignment.EndDate == null || assignment.EndDate >= start),
            cancellationToken);
        if (overlaps)
        {
            return WorkforceProblems.AlreadyOnProject();
        }

        var created = new WorkerAssignment
        {
            WorkerId = workerId,
            ProjectId = projectId,
            DailyRate = rate,
            StartDate = start,
            AssignedAt = clock.UtcNow,
            AssignedBy = caller.UserId,
        };
        db.WorkerAssignments.Add(created);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, WorkerAssignmentConfiguration.OpenIndex))
        {
            return WorkforceProblems.AlreadyOnProject();
        }

        log.WorkerAssigned(caller.UserId, projectId, workerId, created.Id);

        var row = await AssignmentQueries.One(db, projectId, created.Id).SingleAsync(cancellationToken);
        return TypedResults.Created(
            $"/api/projects/{projectId}/workers/{created.Id}",
            WorkerAssignmentView.From(row, caller.Can(PermissionAction.Edit), caller.SeesMoney));
    }
}
