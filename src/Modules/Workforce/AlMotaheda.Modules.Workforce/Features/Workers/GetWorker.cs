using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Assignments;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Workers;

/// <summary>
/// <c>GET /api/workers/{id}</c>: one worker with their assignments, newest first. Only assignments on projects the
/// caller may see are shown: the worker is company-wide, but where they work and at what rate is project data.
/// </summary>
internal static class GetWorker
{
    internal sealed record Response(WorkerView Worker, IReadOnlyList<WorkerAssignmentView> Assignments);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/workers/{workerId:int}", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .WithName("GetWorker")
            .WithSummary("One worker with their project assignments. Rates only with workers See money");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int workerId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var worker = await WorkerQueries.One(db, workerId).SingleOrDefaultAsync(cancellationToken);
        if (worker is null)
        {
            return WorkforceProblems.WorkerNotFound();
        }

        var assignments = await AssignmentQueries.OfWorker(db, workerId).ToListAsync(cancellationToken);
        var visible = await ProjectVisibility.AmongAsync(access, httpContext.User, assignments.Select(row => row.ProjectId), cancellationToken);
        var fullNationalId = caller.Can(PermissionAction.Edit);

        return TypedResults.Ok(new Response(
            WorkerView.From(worker, fullNationalId, caller.SeesMoney),
            assignments
                .Where(row => visible.Contains(row.ProjectId))
                .Select(row => WorkerAssignmentView.From(row, fullNationalId, caller.SeesMoney))
                .ToList()));
    }
}
