using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Projects.Features.Projects;

/// <summary>
/// <c>GET /api/projects/{id}</c>: one project, for the project page and the edit form. A project outside the caller's
/// scope answers 404, exactly like one that doesn't exist.
/// </summary>
internal static class GetProject
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("GetProject")
            .WithSummary("Get one project you may see");

    internal static async Task<Results<Ok<ProjectDetails>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var details = await ProjectDetails.LoadAsync(db, access, users, caller, projectId, cancellationToken);
        return details is null ? ProjectProblems.ProjectNotFound() : TypedResults.Ok(details);
    }
}
