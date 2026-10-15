using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
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
/// <c>PUT /api/projects/{pid}/documents/{id}</c>: change a document's title, category, description and date. The
/// file itself never changes: a wrong file is deleted and uploaded again.
/// </summary>
/// <remarks>The uploader (while they may still upload) or anyone with projects Edit.</remarks>
internal static class UpdateDocument
{
    internal sealed record Request(string? Title, string? Category, string? Description, string? DocumentDate, string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/documents/{documentId:int}", HandleAsync)
            .RequirePermission(ArchiveCaller.Module, PermissionAction.View)
            .WithName("UpdateDocument")
            .WithSummary("Change a document's title, category, description and date (the uploader, or projects Edit)");

    internal static async Task<Results<Ok<DocumentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int documentId,
        Request request,
        HttpContext httpContext,
        ArchiveDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        ArchiveLog log,
        CancellationToken cancellationToken)
    {
        if (ArchiveCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = DocumentFields.Validate(request.Title, request.Category, request.Description, request.DocumentDate, clock.Today, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ArchiveProblems.Validation(errors);
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
            log.ActionRefused(caller.UserId, nameof(UpdateDocument), "not the uploader and no projects Edit");
            return ApiProblem.ForbiddenResult("Only the person who uploaded this document, or someone with projects Edit, can change it.");
        }

        fields!.ApplyTo(document);

        // Always write the row, even when nothing changed, so a stale rowVersion is always refused.
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

        log.DocumentUpdated(caller.UserId, projectId, document.Id);

        var view = await DocumentView.LoadAsync(db, users, caller, project.WritesBlocked, projectId, document.Id, cancellationToken);
        return view is null ? ArchiveProblems.DocumentNotFound() : TypedResults.Ok(view);
    }
}
