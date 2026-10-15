using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Projects.Features.ReportRequests;

/// <summary>
/// <c>GET /api/projects/{pid}/report-requests/{id}</c>: one request with its answer and the daily reports written
/// for it. A request the caller may not see answers 404, like one that doesn't exist.
/// </summary>
internal static class GetReportRequest
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/report-requests/{requestId:int}", HandleAsync)
            .RequirePermission(ReportPermissions.Module, PermissionAction.View)
            .WithName("GetReportRequest")
            .WithSummary("Get one report request with its answer and linked daily reports");

    internal static async Task<Results<Ok<ReportRequestView>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int requestId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var view = await ReportRequestView.LoadAsync(db, users, caller, projectId, requestId, clock.Today, cancellationToken);
        return view is null ? PlanningProblems.ReportRequestNotFound() : TypedResults.Ok(view);
    }
}
