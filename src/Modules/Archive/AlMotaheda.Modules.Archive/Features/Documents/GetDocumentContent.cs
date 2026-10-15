using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Security;
using AlMotaheda.Modules.Archive.Storage;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary>
/// <c>GET /api/projects/{pid}/documents/{id}/content</c>: the file itself, to download (the default) or to show in
/// the browser (<c>disposition=inline</c>, for PDF, images and video only; other kinds always download).
/// </summary>
/// <remarks>
/// <para>The project scope is checked like every other endpoint: a document of a project the caller can't see is
/// a 404, bytes included.</para>
/// <para>The bytes are streamed from the store, never loaded into memory. Range requests work, so a video can seek
/// and a large PDF can load page by page.</para>
/// <para>Every response with bytes is <c>Cache-Control: private, no-store</c> (no shared or browser cache keeps
/// project files), <c>X-Content-Type-Options: nosniff</c> (the browser uses the type the server decided), and
/// <c>Content-Security-Policy: sandbox</c> (a previewed file runs no script and reaches nothing of the app).</para>
/// </remarks>
internal static class GetDocumentContent
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/documents/{documentId:int}/content", HandleAsync)
            .RequirePermission(ArchiveCaller.Module, PermissionAction.View)
            .WithName("GetDocumentContent")
            .WithSummary("Download a document's file, or preview it in the browser with disposition=inline (PDF, images, video)");

    internal static async Task<Results<FileStreamHttpResult, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int documentId,
        HttpContext httpContext,
        ArchiveDbContext db,
        IProjectAccess access,
        IFileStore store,
        ArchiveLog log,
        CancellationToken cancellationToken,
        string? disposition = null)
    {
        var errors = new ValidationErrors();
        var wanted = Input.Clean(disposition)?.ToLowerInvariant() ?? "attachment";
        if (wanted is not ("inline" or "attachment"))
        {
            errors.Add("disposition", "disposition must be inline or attachment.");
            return ArchiveProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ArchiveProblems.ProjectNotFound();
        }

        var file = await DocumentQueries.FileOf(db, projectId, documentId).SingleOrDefaultAsync(cancellationToken);
        if (file is null)
        {
            return ArchiveProblems.DocumentNotFound();
        }

        Stream? content;
        try
        {
            content = await store.OpenReadAsync(file.StorageKey, cancellationToken);
        }
        catch (StorageUnavailableException exception)
        {
            log.StorageFailed(exception, projectId);
            return ArchiveProblems.StorageDown();
        }

        if (content is null)
        {
            log.StoredFileMissing(projectId, file.Id, file.StorageKey);
            return ArchiveProblems.StorageDown();
        }

        var headers = httpContext.Response.Headers;
        headers.CacheControl = "private, no-store";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "sandbox";

        var inline = wanted == "inline" && DocumentPreview.IsAllowed(file.Kind);
        if (inline)
        {
            // filename* carries the original (for example Arabic) name; filename is the ASCII fallback.
            var header = new ContentDispositionHeaderValue("inline");
            header.SetHttpFileName(file.FileName);
            headers.ContentDisposition = header.ToString();
        }

        return TypedResults.Stream(
            content,
            file.ContentType,
            fileDownloadName: inline ? null : file.FileName,
            lastModified: new DateTimeOffset(file.UploadedAt, TimeSpan.Zero),
            entityTag: new EntityTagHeaderValue($"\"{file.Sha256}\""),
            enableRangeProcessing: true);
    }
}
