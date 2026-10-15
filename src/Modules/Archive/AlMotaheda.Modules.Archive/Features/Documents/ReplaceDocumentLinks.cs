using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Data.Configurations;
using AlMotaheda.Modules.Archive.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary>
/// <c>PUT /api/projects/{pid}/documents/{id}/links</c>: replace the list of records a document points to (an
/// expense, a daily report, a subcontract, ...). An empty list removes every link.
/// </summary>
/// <remarks>
/// <para>The uploader (while they may still upload) or anyone with projects Edit.</para>
/// <para>The links are rows of their own, so the document row is written too: its <c>rowVersion</c> then guards the
/// links exactly like the other fields, and two people replacing the links at once can't both win.</para>
/// </remarks>
internal static class ReplaceDocumentLinks
{
    internal sealed record Request(IReadOnlyList<DocumentLinkRequest?>? Links, string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/documents/{documentId:int}/links", HandleAsync)
            .RequirePermission(ArchiveCaller.Module, PermissionAction.View)
            .WithName("ReplaceDocumentLinks")
            .WithSummary("Replace the records a document is linked to (the uploader, or projects Edit)");

    internal static async Task<Results<Ok<DocumentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int documentId,
        Request request,
        HttpContext httpContext,
        ArchiveDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        ArchiveLog log,
        CancellationToken cancellationToken)
    {
        if (ArchiveCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var validLinks = LinkInput.ValidateList(request.Links, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ArchiveProblems.Validation(errors);
        }

        var links = validLinks!;

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ArchiveProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return ArchiveProblems.CompletedProject();
        }

        var document = await db.Documents
            .Include(candidate => candidate.Links)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == documentId && candidate.ProjectId == projectId && candidate.DeletedAt == null,
                cancellationToken);
        if (document is null)
        {
            return ArchiveProblems.DocumentNotFound();
        }

        if (!caller.CanChange(document.UploadedBy))
        {
            log.ActionRefused(caller.UserId, nameof(ReplaceDocumentLinks), "not the uploader and no projects Edit");
            return ApiProblem.ForbiddenResult("Only the person who uploaded this document, or someone with projects Edit, can change its links.");
        }

        // Keep the links that stay, so an unchanged link is never deleted and inserted again.
        foreach (var removed in document.Links.Where(link => !links.Contains((link.EntityType, link.EntityId))).ToList())
        {
            document.Links.Remove(removed);
            db.DocumentLinks.Remove(removed);
        }

        foreach (var (type, id) in links.Where(wanted => !document.Links.Exists(link => link.EntityType == wanted.Type && link.EntityId == wanted.Id)))
        {
            document.Links.Add(new DocumentLink { EntityType = type, EntityId = id });
        }

        db.Entry(document).Property(candidate => candidate.Title).IsModified = true;
        db.Entry(document).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, DocumentLinkConfiguration.UniqueLinkIndex))
        {
            // Not expected behind the row version; a lost race still answers 409, never 500.
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.DocumentLinksReplaced(caller.UserId, projectId, document.Id, links.Count);

        var view = await DocumentView.LoadAsync(db, users, caller, project.WritesBlocked, projectId, document.Id, cancellationToken);
        return view is null ? ArchiveProblems.DocumentNotFound() : TypedResults.Ok(view);
    }
}
