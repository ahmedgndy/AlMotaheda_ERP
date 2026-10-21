using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Storage;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Fleet.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Features.Attachments;

/// <summary>An attachment as screens show it (never the storage key).</summary>
internal sealed record FleetAttachmentView(int Id, string Purpose, string FileName, string ContentType, long SizeBytes, string Sha256, DateTime? CapturedAt, DateTime UploadedAt, int UploadedBy, string? OwnerType, int? OwnerId)
{
    public static FleetAttachmentView From(FleetAttachment attachment) => new(
        attachment.Id, attachment.Purpose.ToString(), attachment.FileName, attachment.ContentType, attachment.SizeBytes, attachment.Sha256, attachment.CapturedAt,
        attachment.UploadedAt, attachment.UploadedBy, attachment.OwnerType, attachment.OwnerId);

    public static async Task<IReadOnlyList<FleetAttachmentView>> OfAsync(FleetDbContext db, string ownerType, int ownerId, CancellationToken cancellationToken) =>
        (await db.Attachments.AsNoTracking().Where(attachment => attachment.OwnerType == ownerType && attachment.OwnerId == ownerId).OrderBy(attachment => attachment.Id).ToListAsync(cancellationToken))
        .Select(From).ToList();
}

/// <summary>
/// <c>POST /api/fleet/attachments</c> (multipart: <c>file</c>, <c>purpose</c>, <c>idempotencyKey</c>, <c>capturedAt</c>):
/// stores one photo or PDF, not yet bound to a record; the record's own save binds it. Needs fuel Create or fleet
/// Create. <c>GET /api/fleet/attachments/{id}/content</c> streams it to its uploader or to whoever may see vehicle
/// records; the URL is never public and the file is never served by type.
/// </summary>
internal static class FleetAttachmentEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/fleet/attachments", UploadAsync)
            .RequireAnyPermission((FleetPermissions.Fuel, PermissionAction.Create), (FleetPermissions.Fleet, PermissionAction.Create))
            .WithName("UploadFleetAttachment")
            .WithSummary("Upload an odometer photo, invoice, receipt or ticket (fuel Create or fleet Create); the type is read from the file's bytes");

        api.MapGet("/fleet/attachments/{attachmentId:int}/content", ContentAsync)
            .RequireAnyPermission((FleetPermissions.Fuel, PermissionAction.View), (FleetPermissions.Fleet, PermissionAction.View))
            .WithName("GetFleetAttachmentContent")
            .WithSummary("The file of an attachment, for its uploader or whoever may see vehicle records");
    }

    internal static async Task<Results<Created<FleetAttachmentView>, Ok<FleetAttachmentView>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        HttpContext httpContext, FleetDbContext db, IFleetFileStore store, IOptions<FleetOptions> settings, CompanyClock clock, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (!httpContext.Request.HasFormContentType)
        {
            return FleetProblems.Validation(Error("file", "Send the photo as multipart/form-data with a 'file' part."));
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
            log.UploadRefused(caller.UserId, FleetProblems.FileTooLargeCode);
            return FleetProblems.TooLarge(maxBytes);
        }
        catch (InvalidDataException)
        {
            return FleetProblems.Validation(Error("file", "The form can't be read. Take the photo again."));
        }

        var errors = new ValidationErrors();
        var purpose = Input.RequiredName<FleetAttachmentPurpose>(form["purpose"].ToString(), errors, "purpose");
        Guid? key = Guid.TryParse(form["idempotencyKey"].ToString(), out var parsedKey) && parsedKey != Guid.Empty ? parsedKey : null;
        if (key is null)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the app for this photo.");
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

        var file = form.Files.GetFile("file");
        if (file is null)
        {
            errors.Add("file", "Take a photo first.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (await db.Attachments.AsNoTracking().SingleOrDefaultAsync(attachment => attachment.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.UploadedBy == caller.UserId ? TypedResults.Ok(FleetAttachmentView.From(repeat)) : FleetProblems.KeyUsedElsewhere();
        }

        if (file!.Length == 0)
        {
            return FleetProblems.EmptyFile();
        }

        PrivateFileTypes.Kind? kind;
        await using (var head = file.OpenReadStream())
        {
            var header = new byte[FleetFileTypes.HeaderLength];
            var read = await head.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            kind = FleetFileTypes.Detect(header.AsSpan(0, read));
        }

        if (kind is null || !FleetFileTypes.Allows(purpose!.Value, kind))
        {
            log.UploadRefused(caller.UserId, FleetProblems.FileTypeNotAllowed);
            return FleetProblems.TypeNotAllowed(purpose is FleetAttachmentPurpose.Invoice or FleetAttachmentPurpose.Supporting ? "JPEG, PNG, WebP, HEIC photos or PDF" : "JPEG, PNG, WebP or HEIC photos");
        }

        var limit = (long)(kind.IsImage ? options.ImageMaxMegabytes : options.DocumentMaxMegabytes) * 1024 * 1024;
        StoredFile stored;
        try
        {
            await using var content = file.OpenReadStream();
            stored = await store.SaveAsync(content, limit, cancellationToken);
        }
        catch (FileTooLargeException)
        {
            log.UploadRefused(caller.UserId, FleetProblems.FileTooLargeCode);
            return FleetProblems.TooLarge(limit);
        }
        catch (StorageUnavailableException exception)
        {
            log.StorageFailed(exception);
            return FleetProblems.StorageDown();
        }

        var attachment = new FleetAttachment
        {
            Purpose = purpose!.Value,
            StorageKey = stored.StorageKey,
            FileName = SafeName(file.FileName, kind),
            ContentType = kind.ContentType,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            CapturedAt = capturedAt,
            IdempotencyKey = key!.Value,
            UploadedBy = caller.UserId,
            UploadedAt = clock.UtcNow,
        };
        db.Attachments.Add(attachment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, FleetAttachmentConfiguration.KeyIndex))
        {
            await RemoveAsync(store, stored.StorageKey);
            db.ChangeTracker.Clear();
            var first = await db.Attachments.AsNoTracking().SingleAsync(candidate => candidate.IdempotencyKey == key, cancellationToken);
            return first.UploadedBy == caller.UserId ? TypedResults.Ok(FleetAttachmentView.From(first)) : FleetProblems.KeyUsedElsewhere();
        }
        catch
        {
            await RemoveAsync(store, stored.StorageKey);
            throw;
        }

        log.AttachmentUploaded(caller.UserId, attachment.Id, attachment.Purpose.ToString());
        return TypedResults.Created($"/api/fleet/attachments/{attachment.Id}/content", FleetAttachmentView.From(attachment));
    }

    internal static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> ContentAsync(
        int attachmentId, HttpContext httpContext, FleetDbContext db, IFleetFileStore store, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == attachmentId, cancellationToken);
        if (attachment is null || !MaySee(caller, attachment))
        {
            return FleetProblems.AttachmentNotFound();
        }

        Stream? stream;
        try
        {
            stream = await store.OpenReadAsync(attachment.StorageKey, cancellationToken);
        }
        catch (StorageUnavailableException exception)
        {
            log.StorageFailed(exception);
            return FleetProblems.StorageDown();
        }

        if (stream is null)
        {
            return FleetProblems.AttachmentNotFound();
        }

        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return TypedResults.File(stream, attachment.ContentType, attachment.FileName);
    }

    /// <summary>The uploader always; anyone who reviews claims or vehicles; a bound file for whoever may see vehicle records.</summary>
    private static bool MaySee(FleetCaller caller, FleetAttachment attachment) =>
        attachment.UploadedBy == caller.UserId
        || caller.CanFuel(PermissionAction.Approve) || caller.CanFleet(PermissionAction.Approve)
        || caller.SeesVehicleMoney
        || (attachment.OwnerType is not null && (caller.CanFleet(PermissionAction.View) || caller.CanFuel(PermissionAction.View)));

    private static string SafeName(string? fileName, PrivateFileTypes.Kind kind)
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

    private static async Task RemoveAsync(IFleetFileStore store, string storageKey)
    {
        try
        {
            await store.DeleteAsync(storageKey, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover file has no row and no valid key: it is never read.
        }
    }
}

/// <summary>Binds uploaded attachments to the record being saved, in the record's transaction.</summary>
internal static class AttachmentBinding
{
    /// <summary>
    /// Loads the attachments the form names. Each must exist, be uploaded by <paramref name="userId"/>, have the
    /// purpose asked, and not be bound to another record. Returns the refusal, or the attachments.
    /// </summary>
    public static async Task<(IReadOnlyList<FleetAttachment> Attachments, ProblemHttpResult? Refused)> LoadAsync(
        FleetDbContext db, IReadOnlyCollection<int> ids, int userId, string ownerType, int? ownerId, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return ([], null);
        }

        if (ids.Count > 10)
        {
            return ([], FleetProblems.BadAttachments("A record takes at most 10 attachments."));
        }

        var found = await db.Attachments.Where(attachment => ids.Contains(attachment.Id)).ToListAsync(cancellationToken);
        if (found.Count != ids.Distinct().Count())
        {
            return ([], FleetProblems.BadAttachments("Some photos were not found. Take them again."));
        }

        if (found.Any(attachment => attachment.UploadedBy != userId))
        {
            return ([], FleetProblems.BadAttachments("Only photos you uploaded yourself can be attached."));
        }

        if (found.Any(attachment => attachment.OwnerType is not null && !(attachment.OwnerType == ownerType && attachment.OwnerId == ownerId)))
        {
            return ([], FleetProblems.BadAttachments("A photo is already attached to another record."));
        }

        return (found, null);
    }

    public static void Bind(IEnumerable<FleetAttachment> attachments, string ownerType, int ownerId, DateTime now)
    {
        foreach (var attachment in attachments.Where(attachment => attachment.OwnerType is null))
        {
            attachment.OwnerType = ownerType;
            attachment.OwnerId = ownerId;
            attachment.BoundAt = now;
        }
    }
}
