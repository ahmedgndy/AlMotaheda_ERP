using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary><c>GET /api/projects/{pid}/documents/{id}</c>: one document's metadata and links.</summary>
internal static class GetDocument
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/documents/{documentId:int}", HandleAsync)
            .RequirePermission(ArchiveCaller.Module, PermissionAction.View)
            .WithName("GetDocument")
            .WithSummary("Get one document's metadata and links");

    internal static async Task<Results<Ok<DocumentView>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int documentId,
        HttpContext httpContext,
        ArchiveDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (ArchiveCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ArchiveProblems.ProjectNotFound();
        }

        var view = await DocumentView.LoadAsync(db, users, caller, project.WritesBlocked, projectId, documentId, cancellationToken);
        return view is null ? ArchiveProblems.DocumentNotFound() : TypedResults.Ok(view);
    }
}
