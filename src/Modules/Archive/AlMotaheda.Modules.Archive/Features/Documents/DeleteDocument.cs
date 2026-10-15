using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary>
/// <c>DELETE /api/projects/{pid}/documents/{id}</c>: remove a document from the archive. A soft delete: the row is
/// hidden from every list and read (404 from then on), who and when are kept, and the file stays in the store.
/// </summary>
/// <remarks>The uploader (while they may still upload) or anyone with projects Edit. Deleting twice answers 404.</remarks>
internal static class DeleteDocument
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapDelete("/{projectId:int}/documents/{documentId:int}", HandleAsync)
            .RequirePermission(ArchiveCaller.Module, PermissionAction.View)
            .WithName("DeleteDocument")
            .WithSummary("Delete a document from the archive; the file is kept (the uploader, or projects Edit)");

    internal static async Task<Results<NoContent, ProblemHttpResult>> HandleAsync(
        int projectId,
        int documentId,
        HttpContext httpContext,
        ArchiveDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        ArchiveLog log,
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

        if (project.WritesBlocked)
        {
            return ArchiveProblems.CompletedProject();
        }

        var document = await db.Documents.SingleOrDefaultAsync(
            candidate => candidate.Id == documentId && candidate.ProjectId == projectId && candidate.DeletedAt == null,
            cancellationToken);
        if (document is null)
        {
            return ArchiveProblems.DocumentNotFound();
        }

        if (!caller.CanChange(document.UploadedBy))
        {
            log.ActionRefused(caller.UserId, nameof(DeleteDocument), "not the uploader and no projects Edit");
            return ApiProblem.ForbiddenResult("Only the person who uploaded this document, or someone with projects Edit, can delete it.");
        }

        document.DeletedAt = clock.UtcNow;
        document.DeletedBy = caller.UserId;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else changed or deleted it between the read and the save.
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.DocumentDeleted(caller.UserId, projectId, document.Id);
        return TypedResults.NoContent();
    }
}
