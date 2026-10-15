using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Security;
using AlMotaheda.Modules.Archive.Storage;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary>
/// <c>POST /api/projects/{pid}/documents</c>: add a file to a project's archive. The body is
/// <c>multipart/form-data</c> with <c>file</c>, <c>title</c>, <c>category</c>, and optionally <c>description</c>,
/// <c>documentDate</c> (yyyy-MM-dd), <c>entityType</c> and <c>entityId</c>. A photo taken with a phone camera is an
/// ordinary upload.
/// </summary>
/// <remarks>
/// <para>Needs projects Create or projects Edit (checked here, because <c>RequirePermission</c> asks for one action).</para>
/// <para>The project scope and the completed-project rule are checked before the body is read, so a caller who may
/// not upload never writes a byte to the disk. Then the file is streamed into the store while its type, size and
/// hash are checked, the fields are validated, and the row is saved. If anything after the store fails (invalid
/// fields, a failed save, a dropped connection), the stored file is removed.</para>
/// <para>Only this endpoint accepts a large body: its request size limit is raised to the largest file limit (video)
/// plus the form around it. Every other endpoint keeps the server default.</para>
/// </remarks>
internal static class UploadDocument
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/documents", HandleAsync)
            // No Accepts("multipart/form-data") metadata: routing would then answer a bare 415 to any other body,
            // and the frontend needs the 400 file_required with its code instead.
            .WithName("UploadDocument")
            .WithSummary("Upload a file (PDF, photo, video, Word, Excel or drawing) to a project's archive (projects Create or Edit)");

    internal static async Task<Results<Created<DocumentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ArchiveDbContext db,
        IProjectAccess access,
        IEnumerable<IProjectRecordOwnership> owners,
        IUserDirectory users,
        IFileStore store,
        IOptions<ArchiveLimitsOptions> limits,
        CompanyClock clock,
        ArchiveLog log,
        CancellationToken cancellationToken)
    {
        if (ArchiveCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (!caller.CanUpload)
        {
            log.ActionRefused(caller.UserId, nameof(UploadDocument), "no projects Create or Edit");
            return ApiProblem.ForbiddenResult();
        }

        if (!UploadForm.TryGetBoundary(httpContext.Request, out var boundary))
        {
            return ArchiveProblems.NoFile();
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

        // Raised for this request only, before the body is read. Kestrel's default (about 28.6 MB) stays everywhere else.
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = limits.Value.MaxRequestBytes;
        }

        var form = new UploadForm();
        var saved = false;
        Document document;
        try
        {
            UploadFailure? failure;
            try
            {
                failure = await form.ReadAsync(httpContext.Request, boundary, projectId, store, limits.Value, cancellationToken);
            }
            catch (StorageUnavailableException exception)
            {
                log.StorageFailed(exception, projectId);
                return ArchiveProblems.StorageDown();
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                log.UploadRefused(caller.UserId, projectId, ArchiveProblems.FileTooLarge);
                return ArchiveProblems.TooLarge(limits.Value.MaxRequestBytes);
            }
            catch (InvalidDataException)
            {
                // A broken multipart body (bad boundary, headers too long): the form can't be read.
                return ArchiveProblems.NoFile();
            }

            if (failure is not null)
            {
                log.UploadRefused(caller.UserId, projectId, failure.Code);
                return failure.Code switch
                {
                    ArchiveProblems.FileEmpty => ArchiveProblems.EmptyFile(),
                    ArchiveProblems.FileTooLarge => ArchiveProblems.TooLarge(failure.MaxBytes),
                    ArchiveProblems.FileExtensionMismatch => ArchiveProblems.ExtensionMismatch(failure.DetectedType ?? "another type"),
                    _ => ArchiveProblems.TypeNotAllowed(),
                };
            }

            var errors = form.Errors;
            var fields = DocumentFields.Validate(
                form.Field("title"), form.Field("category"), form.Field("description"), form.Field("documentDate"), clock.Today, errors);
            var link = LinkInput.ValidateOne(form.Field("entityType"), form.Field("entityId"), errors);

            if (form.File is not { } file)
            {
                if (!errors.HasErrors)
                {
                    return ArchiveProblems.NoFile();
                }

                errors.Add(ArchiveProblems.FileField, "Take a photo or choose a file.");
                return ArchiveProblems.Validation(errors);
            }

            if (errors.HasErrors)
            {
                return ArchiveProblems.Validation(errors);
            }

            // The linked record must be on this project: the owning module says so, not the client.
            if (link is { } wanted
                && !(await RecordOwnership.FindAsync(owners, projectId, wanted.Type.ToString(), [wanted.Id], cancellationToken)).Contains(wanted.Id))
            {
                errors.Add("entityId", "This record was not found on this project.");
                return ArchiveProblems.Validation(errors);
            }

            document = new Document
            {
                ProjectId = projectId,
                FileName = file.FileName,
                ContentType = file.Type.ContentType,
                Kind = file.Type.Kind,
                SizeBytes = file.Stored.SizeBytes,
                Sha256 = file.Stored.Sha256,
                StorageKey = file.Stored.StorageKey,
                UploadedAt = clock.UtcNow,
                UploadedBy = caller.UserId,
            };
            fields!.ApplyTo(document);

            if (link is { } target)
            {
                document.Links.Add(new DocumentLink { EntityType = target.Type, EntityId = target.Id });
            }

            db.Documents.Add(document);
            await db.SaveChangesAsync(cancellationToken);
            saved = true;
        }
        finally
        {
            if (!saved && form.File is { } orphan)
            {
                await RemoveAsync(store, log, projectId, orphan.Stored.StorageKey);
            }
        }

        log.DocumentUploaded(caller.UserId, projectId, document.Id, document.Kind.ToString(), document.Category.ToString(), document.SizeBytes);

        var view = await DocumentView.LoadAsync(db, users, caller, project.WritesBlocked, projectId, document.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/documents/{document.Id}", view!);
    }

    /// <summary>
    /// Removes a stored file whose upload did not complete. Never with the request's token: a cancelled request is
    /// exactly when the file must still go.
    /// </summary>
    private static async Task RemoveAsync(IFileStore store, ArchiveLog log, int projectId, string storageKey)
    {
        try
        {
            await store.DeleteAsync(storageKey, CancellationToken.None);
            log.StoredFileRemoved(projectId, storageKey);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.StoredFileNotRemoved(exception, projectId, storageKey);
        }
    }
}
