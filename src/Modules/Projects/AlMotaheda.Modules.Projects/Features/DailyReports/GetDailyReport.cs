using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Projects.Features.DailyReports;

/// <summary>
/// <c>GET /api/projects/{pid}/daily-reports/{id}</c>: one report with its work, materials, equipment, and the
/// project's supplies received that day.
/// </summary>
internal static class GetDailyReport
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/daily-reports/{reportId:int}", HandleAsync)
            .RequirePermission(SitePermissions.Module, PermissionAction.View)
            .WithName("GetDailyReport")
            .WithSummary("Get one daily site report with its work lines, materials, equipment and the supplies received that day");

    internal static async Task<Results<Ok<DailyReportView>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int reportId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var view = await DailyReportView.LoadAsync(db, users, projectId, reportId, caller.UserId, CanReview(httpContext, project), cancellationToken);
        return view is null ? PlanningProblems.DailyReportNotFound() : TypedResults.Ok(view);
    }

    /// <summary>site Approve, and an Executive or the project's manager.</summary>
    internal static bool CanReview(HttpContext httpContext, ProjectAccessInfo project) =>
        httpContext.User.HasPermission(SitePermissions.Module, PermissionAction.Approve) && DailyReportRules.CanReview(project);
}
