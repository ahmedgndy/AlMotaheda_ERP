using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Features.Attachments;

/// <summary>An attachment as screens show it (never the storage key).</summary>
internal sealed record OfficeAttachmentView(
    int Id,
    string Purpose,
    string FileName,
    string ContentType,
    long SizeBytes,
    string CaptureSource,
    DateTime? CapturedAt,
    string? QualityNote,
    DateTime UploadedAt,
    int UploadedBy)
{
    public static OfficeAttachmentView From(OfficeAttachment attachment) => new(
        attachment.Id, attachment.Purpose.ToString(), attachment.FileName, attachment.ContentType, attachment.SizeBytes, attachment.CaptureSource,
        attachment.CapturedAt, attachment.QualityNote, attachment.UploadedAt, attachment.UploadedBy);

    public static async Task<IReadOnlyList<OfficeAttachmentView>> OfAsync(OfficeDbContext db, string ownerType, int ownerId, CancellationToken cancellationToken) =>
        (await db.Attachments.AsNoTracking()
            .Where(attachment => attachment.OwnerType == ownerType && attachment.OwnerId == ownerId)
            .OrderBy(attachment => attachment.Id)
            .ToListAsync(cancellationToken))
        .Select(From)
        .ToList();
}

/// <summary>
/// <c>POST /api/office/attachments</c> (multipart: <c>file</c>, <c>purpose</c>, <c>idempotencyKey</c>,
/// <c>captureSource</c>, <c>capturedAt</c>, <c>deviceModel</c>, <c>qualityNote</c>): stores one photo or PDF, not yet
/// bound to a record. The record's own save binds it (<see cref="AttachmentBinding"/>). Needs office Create.
/// <c>GET /api/office/attachments/{id}/content</c> streams it to whoever may see its record (or its uploader).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>The type comes from the first bytes: JPEG, PNG, WebP or HEIC photos; PDF only for a quotation, a transfer
///   slip or a supporting paper. Anything else is 415.</item>
///   <item>The same idempotency key from the same user returns the first upload (200), so a retried upload from the
///   offline queue stores the file once.</item>
///   <item>"Camera only" is enforced by the app, which offers no gallery; the server records the app's
///   <c>captureSource</c> and cannot prove it (see the module README).</item>
/// </list>
/// </remarks>
internal static class OfficeAttachmentEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/office/attachments", UploadAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Create)
            .WithName("UploadOfficeAttachment")
            .WithSummary("Upload a receipt, repair photo, quotation or cash-count photo (office Create)");

        api.MapGet("/office/attachments/{attachmentId:int}/content", ContentAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeAttachmentContent")
            .WithSummary("The file of an office attachment, for people who may see its record");
    }

    internal static async Task<Results<Created<OfficeAttachmentView>, Ok<OfficeAttachmentView>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        HttpContext httpContext,
        OfficeDbContext db,
        IOfficeFileStore store,
        IOptions<OfficeOptions> settings,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (!httpContext.Request.HasFormContentType)
        {
            return OfficeProblems.Validation(Error("file", "Send the photo as multipart/form-data with a 'file' part."));
        }

        var options = settings.Value;
        var maxBytes = (long)Math.Max(options.ImageMaxMegabytes, options.DocumentMaxMegabytes) * 1024 * 1024;
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
        {
            bodySize.MaxRequestBodySize = maxBytes + 1024 * 1024;
        }

        IFormCollection form;
        try
        {
            form = await httpContext.Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            log.UploadRefused(caller.UserId, OfficeProblems.FileTooLargeCode);
            return OfficeProblems.TooLarge(maxBytes);
        }
        catch (InvalidDataException)
        {
            return OfficeProblems.Validation(Error("file", "The form can't be read. Take the photo again."));
        }

        var errors = new ValidationErrors();
        var purpose = Input.RequiredName<OfficeAttachmentPurpose>(form["purpose"].ToString(), errors, "purpose");
        Guid? key = Guid.TryParse(form["idempotencyKey"].ToString(), out var parsedKey) && parsedKey != Guid.Empty ? parsedKey : null;
        if (key is null)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the app for this photo.");
        }

        var captureSource = Input.Clean(form["captureSource"].ToString()) ?? "Camera";
        if (captureSource is not ("Camera" or "Scanner" or "Document"))
        {
            errors.Add("captureSource", "captureSource must be Camera, Scanner or Document.");
        }

        DateTime? capturedAt = null;
        if (Input.Clean(form["capturedAt"].ToString()) is { } capturedText)
        {
            if (DateTimeOffset.TryParse(capturedText, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var moment))
            {
                capturedAt = moment.UtcDateTime;
            }
            else
            {
                errors.Add("capturedAt", "capturedAt must be an ISO 8601 moment.");
            }
        }

        var deviceModel = Input.OptionalText(form["deviceModel"].ToString(), Data.Configurations.OfficeLengths.Device, errors, "deviceModel", "The device model");
        var qualityNote = Input.OptionalText(form["qualityNote"].ToString(), Data.Configurations.OfficeLengths.Description, errors, "qualityNote", "The quality note");
        var file = form.Files.GetFile("file");
        if (file is null)
        {
            errors.Add("file", "Take a photo first.");
        }

        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        if (await db.Attachments.AsNoTracking().SingleOrDefaultAsync(attachment => attachment.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.UploadedBy == caller.UserId ? TypedResults.Ok(OfficeAttachmentView.From(repeat)) : OfficeProblems.KeyUsedElsewhere();
        }

        if (file!.Length == 0)
        {
            return OfficeProblems.EmptyFile();
        }

        // The type is decided from the first bytes, never from the name or the content type the phone sends.
        OfficeFileTypes.Kind? kind;
        await using (var head = file.OpenReadStream())
        {
            var header = new byte[OfficeFileTypes.HeaderLength];
            var read = await head.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            kind = OfficeFileTypes.Detect(header.AsSpan(0, read));
        }

        if (kind is null || !OfficeFileTypes.Allows(purpose!.Value, kind))
        {
            log.UploadRefused(caller.UserId, OfficeProblems.FileTypeNotAllowed);
            return OfficeProblems.TypeNotAllowed(purpose is OfficeAttachmentPurpose.Quotation or OfficeAttachmentPurpose.Transfer or OfficeAttachmentPurpose.Supporting
                ? "JPEG, PNG, WebP, HEIC photos or PDF"
                : "JPEG, PNG, WebP or HEIC photos");
        }

        var limit = (long)(kind.IsImage ? options.ImageMaxMegabytes : options.DocumentMaxMegabytes) * 1024 * 1024;
        OfficeStoredFile stored;
        try
        {
            await using var content = file.OpenReadStream();
            stored = await store.SaveAsync(content, limit, cancellationToken);
        }
        catch (OfficeFileTooLargeException)
        {
            log.UploadRefused(caller.UserId, OfficeProblems.FileTooLargeCode);
            return OfficeProblems.TooLarge(limit);
        }
        catch (OfficeStorageUnavailableException exception)
        {
            log.StorageFailed(exception);
            return OfficeProblems.StorageDown();
        }

        var attachment = new OfficeAttachment
        {
            Purpose = purpose!.Value,
            StorageKey = stored.StorageKey,
            FileName = SafeName(file.FileName, kind),
            ContentType = kind.ContentType,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            CaptureSource = captureSource,
            CapturedAt = capturedAt,
            DeviceModel = deviceModel,
            QualityNote = qualityNote,
            IdempotencyKey = key!.Value,
            UploadedBy = caller.UserId,
            UploadedAt = clock.UtcNow,
        };
        db.Attachments.Add(attachment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, Data.Configurations.OfficeAttachmentConfiguration.KeyIndex))
        {
            await RemoveAsync(store, log, stored.StorageKey);
            db.ChangeTracker.Clear();
            var first = await db.Attachments.AsNoTracking().SingleAsync(candidate => candidate.IdempotencyKey == key, cancellationToken);
            return first.UploadedBy == caller.UserId ? TypedResults.Ok(OfficeAttachmentView.From(first)) : OfficeProblems.KeyUsedElsewhere();
        }
        catch
        {
            await RemoveAsync(store, log, stored.StorageKey);
            throw;
        }

        log.AttachmentUploaded(caller.UserId, attachment.Id, attachment.Purpose.ToString());
        return TypedResults.Created($"/api/office/attachments/{attachment.Id}/content", OfficeAttachmentView.From(attachment));
    }

    internal static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> ContentAsync(
        int attachmentId,
        HttpContext httpContext,
        OfficeDbContext db,
        IOfficeFileStore store,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == attachmentId, cancellationToken);
        if (attachment is null || !await MaySeeAsync(db, caller, attachment, cancellationToken))
        {
            return OfficeProblems.AttachmentNotFound();
        }

        Stream? stream;
        try
        {
            stream = await store.OpenReadAsync(attachment.StorageKey, cancellationToken);
        }
        catch (OfficeStorageUnavailableException exception)
        {
            log.StorageFailed(exception);
            return OfficeProblems.StorageDown();
        }

        if (stream is null)
        {
            return OfficeProblems.AttachmentNotFound();
        }

        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return TypedResults.File(stream, attachment.ContentType, attachment.FileName);
    }

    /// <summary>The uploader always; anyone else only when they may see the custody of the record it belongs to.</summary>
    private static async Task<bool> MaySeeAsync(OfficeDbContext db, OfficeCaller caller, OfficeAttachment attachment, CancellationToken cancellationToken)
    {
        if (attachment.UploadedBy == caller.UserId || caller.SeesAll)
        {
            return true;
        }

        if (attachment.OwnerId is not { } ownerId)
        {
            return false;
        }

        IQueryable<int> custodyIds = attachment.OwnerType switch
        {
            OfficeAttachmentOwners.Expense => db.Expenses.Where(record => record.Id == ownerId).Select(record => record.CustodyId),
            OfficeAttachmentOwners.FundingRequest => db.FundingRequests.Where(record => record.Id == ownerId).Select(record => record.CustodyId),
            OfficeAttachmentOwners.SpendRequest => db.SpendRequests.Where(record => record.Id == ownerId).Select(record => record.CustodyId),
            OfficeAttachmentOwners.CashCount => db.CashCounts.Where(record => record.Id == ownerId).Select(record => record.CustodyId),
            _ => Enumerable.Empty<int>().AsQueryable(),
        };

        return await db.Custodies.AnyAsync(custody => custodyIds.Contains(custody.Id) && custody.HolderUserId == caller.UserId, cancellationToken);
    }

    private static string SafeName(string? fileName, OfficeFileTypes.Kind kind)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? "");
        var clean = new string(name.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ' ').ToArray()).Trim();
        if (clean.Length == 0)
        {
            clean = "photo";
        }

        if (clean.Length > 150)
        {
            clean = clean[..150];
        }

        return clean + kind.Extension;
    }

    private static ValidationErrors Error(string field, string message)
    {
        var errors = new ValidationErrors();
        errors.Add(field, message);
        return errors;
    }

    private static async Task RemoveAsync(IOfficeFileStore store, OfficeLog log, string storageKey)
    {
        try
        {
            await store.DeleteAsync(storageKey, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.StoredFileNotRemoved(exception, storageKey);
        }
    }
}

/// <summary>Binds uploaded attachments to the record being saved, in the record's transaction.</summary>
internal static class AttachmentBinding
{
    /// <summary>
    /// Loads the attachments the form names. Each must exist, be uploaded by <paramref name="userId"/>, and not be bound
    /// yet (or be bound to this same record already, for a retried save). Returns the refusal, or the attachments.
    /// </summary>
    public static async Task<(IReadOnlyList<OfficeAttachment> Attachments, ProblemHttpResult? Refused)> LoadAsync(
        OfficeDbContext db, IReadOnlyCollection<int> ids, int userId, string ownerType, int? ownerId, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return ([], null);
        }

        if (ids.Count > 20)
        {
            return ([], OfficeProblems.BadAttachments("A record takes at most 20 attachments."));
        }

        var found = await db.Attachments.Where(attachment => ids.Contains(attachment.Id)).ToListAsync(cancellationToken);
        if (found.Count != ids.Distinct().Count())
        {
            return ([], OfficeProblems.BadAttachments("Some photos were not found. Take them again."));
        }

        if (found.Any(attachment => attachment.UploadedBy != userId))
        {
            return ([], OfficeProblems.BadAttachments("Only photos you took yourself can be attached."));
        }

        if (found.Any(attachment => attachment.OwnerType is not null && !(attachment.OwnerType == ownerType && attachment.OwnerId == ownerId)))
        {
            return ([], OfficeProblems.BadAttachments("A photo is already attached to another record."));
        }

        return (found, null);
    }

    public static void Bind(IEnumerable<OfficeAttachment> attachments, string ownerType, int ownerId, DateTime now)
    {
        foreach (var attachment in attachments.Where(attachment => attachment.OwnerType is null))
        {
            attachment.OwnerType = ownerType;
            attachment.OwnerId = ownerId;
            attachment.BoundAt = now;
        }
    }
}
