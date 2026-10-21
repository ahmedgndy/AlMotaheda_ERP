using System.Text.Json;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Storage;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using AlMotaheda.Modules.HR.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.HR.Features.Requests;

internal sealed record HrRequestAttachmentView(int Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAt, int UploadedBy, int? MessageId);

internal sealed record HrRequestMessageView(int Id, int AuthorUserId, HrPersonRef? Author, bool FromHr, string Text, DateTime CreatedAt, IReadOnlyList<HrRequestAttachmentView> Attachments);

/// <summary>A request as the employee and HR read it. The employee sees their own only; HR sees every one.</summary>
internal sealed record HrRequestView(
    int Id,
    string Number,
    int EmployeeId,
    string EmployeeNo,
    string EmployeeNameAr,
    string EmployeeNameEn,
    string Type,
    string Subject,
    string Details,
    DateOnly? FromDate,
    DateOnly? ToDate,
    int? ReferralId,
    string Status,
    bool IsOpen,
    DateTime SubmittedAt,
    HrPersonRef? SubmittedBy,
    DateTime? ReviewStartedAt,
    HrPersonRef? ReviewedBy,
    DateTime? DecidedAt,
    HrPersonRef? DecidedBy,
    string? DecisionNote,
    IReadOnlyList<HrRequestMessageView> Messages,
    IReadOnlyList<HrRequestAttachmentView> Attachments,
    string RowVersion);

/// <summary>What an employee may know of a case referred about them: the subject, where it stands, the outcome. Never the referrer's notes.</summary>
internal sealed record MyReferralView(int Id, string Number, string SourceType, string Subject, decimal? Amount, string Status, DateTime ReferredAt, string? Outcome, string? ClosingNote, DateTime? ClosedAt, bool CanRespond);

/// <summary>
/// HR requests (section 12.D). Self-service for the signed-in employee (any user whose login is linked to an
/// employee): <c>GET/POST /api/hr/my/requests</c>, <c>GET /api/hr/my/requests/{id}</c>, <c>POST …/messages</c>
/// (answer HR's question), <c>POST /api/hr/my/attachments</c> (evidence, multipart), <c>GET /api/hr/my/referrals</c>
/// (cases referred about them, what they may know). HR's side: <c>GET /api/hr/requests</c> and <c>/{id}</c> (hr View),
/// <c>POST /{id}/start-review</c>, <c>/request-info</c>, <c>/messages</c> (hr Edit), <c>POST /{id}/approve</c>,
/// <c>/reject</c>, <c>/close</c> (hr Approve, never one's own request). <c>GET /api/hr/attachments/{id}/content</c>
/// streams a file to its uploader or to HR. Nobody decides their own request; nobody reads another employee's.
/// An approved request changes no payroll by itself: HR acts on it through the payroll inputs, with their rules.
/// </summary>
internal static class HrRequestEndpoints
{
    private const string Owner = "HrRequest";

    internal sealed record SubmitRequest(string? Type, string? Subject, string? Details, string? FromDate, string? ToDate, int? ReferralId, IReadOnlyList<int>? AttachmentIds, string? IdempotencyKey);

    internal sealed record MessageRequest(string? Text, IReadOnlyList<int>? AttachmentIds, string? RowVersion);

    internal sealed record DecisionRequest(string? Note, string? RowVersion);

    internal sealed record ListResponse(IReadOnlyList<HrRequestView> Items, int Page, int PageSize, int TotalCount, IReadOnlyDictionary<string, int> CountsByStatus);

    internal sealed record MyReferralsResponse(IReadOnlyList<MyReferralView> Items);

    public static void Map(IEndpointRouteBuilder api)
    {
        // The employee's own.
        api.MapGet("/hr/my/requests", ListMineAsync)
            .WithName("ListMyHrRequests")
            .WithSummary("The signed-in employee's HR requests with their status (status, page, pageSize). 404 not_an_employee when the login is not linked to an employee");
        api.MapGet("/hr/my/requests/{requestId:int}", GetMineAsync)
            .WithName("GetMyHrRequest")
            .WithSummary("One of the signed-in employee's requests with HR's answers");
        api.MapPost("/hr/my/requests", SubmitAsync)
            .WithName("SubmitHrRequest")
            .WithSummary("Submit an HR request (attendance correction, leave, permission, overtime clarification, payroll question, case response, other), with evidence. Idempotent by idempotencyKey");
        api.MapPost("/hr/my/requests/{requestId:int}/messages", ReplyAsync)
            .WithName("ReplyToHrRequest")
            .WithSummary("Answer HR on one's own request (a NeedsInfo request goes back to HR's queue)");
        api.MapPost("/hr/my/attachments", UploadAsync)
            .DisableAntiforgery()
            .WithName("UploadHrAttachment")
            .WithSummary("Upload evidence for an HR request (multipart: file, idempotencyKey); the type is read from the file's bytes");
        api.MapGet("/hr/my/referrals", MyReferralsAsync)
            .WithName("ListMyHrReferrals")
            .WithSummary("The cases referred to HR about the signed-in employee: subject, status, outcome; never the referrer's notes");

        // HR's queue.
        api.MapGet("/hr/requests", ListAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListHrRequests")
            .WithSummary("Every employee's HR requests (status, type, employeeId; page, pageSize), with counts per status");
        api.MapGet("/hr/requests/{requestId:int}", GetAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("GetHrRequest")
            .WithSummary("One HR request with its thread and evidence");
        api.MapPost("/hr/requests/{requestId:int}/start-review", StartReviewAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("StartHrRequestReview")
            .WithSummary("Take a submitted request under review (hr Edit; never one's own)");
        api.MapPost("/hr/requests/{requestId:int}/request-info", RequestInfoAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("RequestHrRequestInfo")
            .WithSummary("Ask the employee for more (hr Edit; a message is required): the request waits for the employee");
        api.MapPost("/hr/requests/{requestId:int}/messages", AnswerAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("AnswerHrRequest")
            .WithSummary("Write to the employee on their request (hr Edit) without changing its status");
        api.MapPost("/hr/requests/{requestId:int}/approve", (int requestId, DecisionRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
                => DecideAsync(HrRequestStatus.Approved, requestId, request, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(HrPermissions.Module, PermissionAction.Approve)
            .WithName("ApproveHrRequest")
            .WithSummary("Approve a request with a note (hr Approve; never one's own). Final. Changes no payroll by itself");
        api.MapPost("/hr/requests/{requestId:int}/reject", (int requestId, DecisionRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
                => DecideAsync(HrRequestStatus.Rejected, requestId, request, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(HrPermissions.Module, PermissionAction.Approve)
            .WithName("RejectHrRequest")
            .WithSummary("Reject a request with a note (hr Approve; never one's own). Final");
        api.MapPost("/hr/requests/{requestId:int}/close", (int requestId, DecisionRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
                => DecideAsync(HrRequestStatus.Closed, requestId, request, httpContext, db, users, clock, log, cancellationToken))
            .RequirePermission(HrPermissions.Module, PermissionAction.Approve)
            .WithName("CloseHrRequest")
            .WithSummary("Close a request that needed an answer, not a decision, with a note (hr Approve; never one's own). Final");
        api.MapGet("/hr/attachments/{attachmentId:int}/content", ContentAsync)
            .WithName("GetHrAttachmentContent")
            .WithSummary("The file of an HR request attachment, for its uploader or HR (hr View)");
    }

    // ---- The employee's own -------------------------------------------------------------------------------------

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListMineAsync(
        HttpContext httpContext, HrDbContext db, IUserDirectory users, CancellationToken cancellationToken, string? status = null, int page = 1, int pageSize = 25)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await MeAsync(db, caller, cancellationToken) is not { } me)
        {
            return HrProblems.NotEmployee();
        }

        return await PageAsync(db, users, db.Requests.AsNoTracking().Where(request => request.EmployeeId == me.Id), status, null, null, page, pageSize, cancellationToken);
    }

    internal static async Task<Results<Ok<HrRequestView>, ProblemHttpResult>> GetMineAsync(int requestId, HttpContext httpContext, HrDbContext db, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await MeAsync(db, caller, cancellationToken) is not { } me)
        {
            return HrProblems.NotEmployee();
        }

        var request = await Load(db).SingleOrDefaultAsync(candidate => candidate.Id == requestId && candidate.EmployeeId == me.Id, cancellationToken);
        return request is null ? HrProblems.RequestNotFound() : TypedResults.Ok(await ViewAsync(request, users, cancellationToken));
    }

    internal static async Task<Results<Created<HrRequestView>, Ok<HrRequestView>, ValidationProblem, ProblemHttpResult>> SubmitAsync(
        SubmitRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var type = Input.RequiredName<HrRequestType>(request.Type, errors, "type");
        var subject = Input.RequiredText(request.Subject, HrRequestConfiguration.SubjectMaxLength, errors, "subject", "The subject");
        var details = Input.RequiredText(request.Details, HrRequestConfiguration.DetailsMaxLength, errors, "details", "The details");
        var fromDate = Input.OptionalDate(request.FromDate, errors, "fromDate", "The first day");
        var toDate = Input.OptionalDate(request.ToDate, errors, "toDate", "The last day");
        if (fromDate is { } first && toDate is { } last && last < first)
        {
            errors.Add("toDate", "The last day can't be before the first.");
        }

        if (type is HrRequestType.Leave or HrRequestType.Permission or HrRequestType.AttendanceCorrection && fromDate is null)
        {
            errors.Add("fromDate", "A leave, a permission or an attendance correction needs its first day.");
        }

        if (type == HrRequestType.CaseResponse && request.ReferralId is null or < 1)
        {
            errors.Add("referralId", "A case response names the referral it answers.");
        }
        else if (type != HrRequestType.CaseResponse && request.ReferralId is not null)
        {
            errors.Add("referralId", "Only a case response names a referral.");
        }

        Guid? key = Guid.TryParse(request.IdempotencyKey, out var parsedKey) && parsedKey != Guid.Empty ? parsedKey : null;
        if (key is null)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the app for this request.");
        }

        if (request.AttachmentIds is { Count: > 20 })
        {
            errors.Add("attachmentIds", "At most 20 attachments.");
        }

        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        if (await MeAsync(db, caller, cancellationToken) is not { } me)
        {
            return HrProblems.NotEmployee();
        }

        if (await db.Requests.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.EmployeeId == me.Id
                ? TypedResults.Ok(await ViewAsync((await Load(db).SingleAsync(candidate => candidate.Id == repeat.Id, cancellationToken)), users, cancellationToken))
                : HrProblems.KeyUsedElsewhere();
        }

        if (type == HrRequestType.CaseResponse && !await db.Referrals.AsNoTracking().AnyAsync(referral => referral.Id == request.ReferralId && referral.EmployeeId == me.Id, cancellationToken))
        {
            errors.Add("referralId", "No case about you has this id.");
            return HrProblems.Validation(errors);
        }

        var attachments = await AttachmentsAsync(db, request.AttachmentIds, caller.UserId, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var now = clock.UtcNow;
        var record = new HrRequest
        {
            EmployeeId = me.Id,
            Type = type!.Value,
            Subject = subject!,
            Details = details!,
            FromDate = fromDate,
            ToDate = toDate,
            ReferralId = request.ReferralId,
            IdempotencyKey = key!.Value,
            SubmittedAt = now,
            SubmittedBy = caller.UserId,
        };
        foreach (var attachment in attachments)
        {
            attachment.Request = record;
        }

        db.Requests.Add(record);
        await AlertHrAsync(db, users, record, caller.UserId, now, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, HrRequestConfiguration.IdempotencyIndex))
        {
            var existing = await Load(db).SingleAsync(candidate => candidate.IdempotencyKey == key, cancellationToken);
            return existing.EmployeeId == me.Id ? TypedResults.Ok(await ViewAsync(existing, users, cancellationToken)) : HrProblems.KeyUsedElsewhere();
        }

        log.RequestSubmitted(caller.UserId, record.Id, record.Type.ToString(), me.Id);
        return TypedResults.Created($"/api/hr/my/requests/{record.Id}", await ViewAsync(await Load(db).SingleAsync(candidate => candidate.Id == record.Id, cancellationToken), users, cancellationToken));
    }

    internal static async Task<Results<Ok<HrRequestView>, ValidationProblem, ProblemHttpResult>> ReplyAsync(
        int requestId, MessageRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var text = Input.RequiredText(request.Text, HrRequestMessageConfiguration.TextMaxLength, errors, "text", "The message");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        if (await MeAsync(db, caller, cancellationToken) is not { } me)
        {
            return HrProblems.NotEmployee();
        }

        var record = await Load(db).SingleOrDefaultAsync(candidate => candidate.Id == requestId && candidate.EmployeeId == me.Id, cancellationToken);
        if (record is null)
        {
            return HrProblems.RequestNotFound();
        }

        if (!record.IsOpen)
        {
            return HrProblems.WrongStatus("This request was decided; submit a new one.");
        }

        var attachments = await AttachmentsAsync(db, request.AttachmentIds, caller.UserId, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var now = clock.UtcNow;
        var message = new HrRequestMessage { Request = record, AuthorUserId = caller.UserId, FromHr = false, Text = text!, CreatedAt = now };
        record.Messages.Add(message);
        foreach (var attachment in attachments)
        {
            attachment.Request = record;
            attachment.MessageId = null;
            record.Attachments.Add(attachment);
        }

        if (record.Status == HrRequestStatus.NeedsInfo)
        {
            record.Resubmit(now);
        }
        else
        {
            record.UpdatedAt = now;
        }

        await AlertHrAsync(db, users, record, caller.UserId, now, cancellationToken);
        db.Entry(record).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        foreach (var attachment in attachments)
        {
            attachment.MessageId = message.Id;
        }

        await db.SaveChangesAsync(cancellationToken);
        log.RequestMessage(caller.UserId, record.Id);
        return TypedResults.Ok(await ViewAsync(await Load(db).SingleAsync(candidate => candidate.Id == record.Id, cancellationToken), users, cancellationToken));
    }

    internal static async Task<Results<Created<HrRequestAttachmentView>, Ok<HrRequestAttachmentView>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        HttpContext httpContext, HrDbContext db, IHrFileStore store, IOptions<HrStorageOptions> settings, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (!httpContext.Request.HasFormContentType)
        {
            errors.Add("file", "Send the file as multipart/form-data with a 'file' part.");
            return HrProblems.Validation(errors);
        }

        IFormCollection form;
        try
        {
            form = await httpContext.Request.ReadFormAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException)
        {
            errors.Add("file", "The form can't be read. Try again.");
            return HrProblems.Validation(errors);
        }

        Guid? key = Guid.TryParse(form["idempotencyKey"].ToString(), out var parsedKey) && parsedKey != Guid.Empty ? parsedKey : null;
        if (key is null)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the app for this file.");
        }

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            errors.Add("file", "Choose a file first.");
        }

        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        if (await db.RequestAttachments.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.UploadedBy == caller.UserId ? TypedResults.Ok(View(repeat)) : HrProblems.KeyUsedElsewhere();
        }

        PrivateFileTypes.Kind? kind;
        await using (var probe = file!.OpenReadStream())
        {
            var header = new byte[PrivateFileTypes.HeaderLength];
            var read = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            kind = PrivateFileTypes.Detect(header.AsSpan(0, read));
        }

        if (kind is null)
        {
            return HrProblems.FileNotAllowed();
        }

        var maxBytes = settings.Value.MaxMegabytes * 1024L * 1024L;
        StoredFile stored;
        try
        {
            await using var content = file.OpenReadStream();
            stored = await store.SaveAsync(content, maxBytes, cancellationToken);
        }
        catch (FileTooLargeException)
        {
            return HrProblems.TooLarge(settings.Value.MaxMegabytes);
        }
        catch (StorageUnavailableException)
        {
            return HrProblems.StorageDown();
        }

        var attachment = new HrRequestAttachment
        {
            FileName = SafeName(file.FileName, kind),
            ContentType = kind.ContentType,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            StorageKey = stored.StorageKey,
            IdempotencyKey = key!.Value,
            UploadedAt = clock.UtcNow,
            UploadedBy = caller.UserId,
        };
        db.RequestAttachments.Add(attachment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, HrRequestAttachmentConfiguration.IdempotencyIndex))
        {
            await store.DeleteAsync(stored.StorageKey, CancellationToken.None);
            var first = await db.RequestAttachments.AsNoTracking().SingleAsync(candidate => candidate.IdempotencyKey == key, cancellationToken);
            return first.UploadedBy == caller.UserId ? TypedResults.Ok(View(first)) : HrProblems.KeyUsedElsewhere();
        }

        log.AttachmentUploaded(caller.UserId, attachment.Id, attachment.SizeBytes);
        return TypedResults.Created($"/api/hr/attachments/{attachment.Id}/content", View(attachment));
    }

    internal static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> ContentAsync(int attachmentId, HttpContext httpContext, HrDbContext db, IHrFileStore store, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var attachment = await db.RequestAttachments.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == attachmentId, cancellationToken);
        if (attachment is null || (attachment.UploadedBy != caller.UserId && !caller.Can(PermissionAction.View)))
        {
            return HrProblems.AttachmentNotFound();
        }

        Stream? content;
        try
        {
            content = await store.OpenReadAsync(attachment.StorageKey, cancellationToken);
        }
        catch (StorageUnavailableException)
        {
            return HrProblems.StorageDown();
        }

        if (content is null)
        {
            return HrProblems.AttachmentNotFound();
        }

        httpContext.Response.Headers.CacheControl = "private, no-store";
        httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return TypedResults.File(content, attachment.ContentType, attachment.FileName, enableRangeProcessing: false);
    }

    internal static async Task<Results<Ok<MyReferralsResponse>, ProblemHttpResult>> MyReferralsAsync(HttpContext httpContext, HrDbContext db, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await MeAsync(db, caller, cancellationToken) is not { } me)
        {
            return HrProblems.NotEmployee();
        }

        var referrals = await db.Referrals.AsNoTracking().Where(referral => referral.EmployeeId == me.Id).OrderByDescending(referral => referral.Id).Take(100).ToListAsync(cancellationToken);
        var answered = await db.Requests.AsNoTracking().Where(request => request.EmployeeId == me.Id && request.ReferralId != null).Select(request => request.ReferralId!.Value).ToListAsync(cancellationToken);
        return TypedResults.Ok(new MyReferralsResponse(referrals.Select(referral => new MyReferralView(
            referral.Id, referral.Number, referral.SourceType, referral.Subject, referral.Amount, referral.Status.ToString(), referral.ReferredAt,
            referral.Outcome?.ToString(), referral.Status == HrReferralStatus.Closed ? referral.ClosingNote : null, referral.ClosedAt,
            referral.Status != HrReferralStatus.Closed && !answered.Contains(referral.Id))).ToList()));
    }

    // ---- HR's queue ----------------------------------------------------------------------------------------------

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, HrDbContext db, IUserDirectory users, CancellationToken cancellationToken,
        string? status = null, string? type = null, int? employeeId = null, int page = 1, int pageSize = 25)
    {
        if (HrCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        return await PageAsync(db, users, db.Requests.AsNoTracking(), status, type, employeeId, page, pageSize, cancellationToken);
    }

    internal static async Task<Results<Ok<HrRequestView>, ProblemHttpResult>> GetAsync(int requestId, HttpContext httpContext, HrDbContext db, IUserDirectory users, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var request = await Load(db).SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);
        return request is null ? HrProblems.RequestNotFound() : TypedResults.Ok(await ViewAsync(request, users, cancellationToken));
    }

    internal static async Task<Results<Ok<HrRequestView>, ValidationProblem, ProblemHttpResult>> StartReviewAsync(
        int requestId, DecisionRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var record = await Load(db).SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);
        if (record is null)
        {
            return HrProblems.RequestNotFound();
        }

        if (record.SubmittedBy == caller.UserId)
        {
            log.RequestRefused(caller.UserId, "StartReview", record.Id, "own request");
            return HrProblems.ReviewerIsSubmitter();
        }

        if (!record.CanMoveTo(HrRequestStatus.UnderReview))
        {
            return HrProblems.WrongStatus($"A {record.Status} request can't be taken under review.");
        }

        record.StartReview(caller.UserId, clock.UtcNow);
        return await SaveAsync(record, rowVersion!, db, users, log, caller.UserId, cancellationToken);
    }

    internal static async Task<Results<Ok<HrRequestView>, ValidationProblem, ProblemHttpResult>> RequestInfoAsync(
        int requestId, MessageRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken) =>
        await WriteAsync(requestId, request, askForInfo: true, httpContext, db, users, clock, log, cancellationToken);

    internal static async Task<Results<Ok<HrRequestView>, ValidationProblem, ProblemHttpResult>> AnswerAsync(
        int requestId, MessageRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken) =>
        await WriteAsync(requestId, request, askForInfo: false, httpContext, db, users, clock, log, cancellationToken);

    private static async Task<Results<Ok<HrRequestView>, ValidationProblem, ProblemHttpResult>> WriteAsync(
        int requestId, MessageRequest request, bool askForInfo, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var text = Input.RequiredText(request.Text, HrRequestMessageConfiguration.TextMaxLength, errors, "text", "The message");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var record = await Load(db).SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);
        if (record is null)
        {
            return HrProblems.RequestNotFound();
        }

        if (record.SubmittedBy == caller.UserId)
        {
            log.RequestRefused(caller.UserId, askForInfo ? "RequestInfo" : "Answer", record.Id, "own request");
            return HrProblems.ReviewerIsSubmitter();
        }

        if (!record.IsOpen || (askForInfo && !record.CanMoveTo(HrRequestStatus.NeedsInfo)))
        {
            return HrProblems.WrongStatus($"A {record.Status} request takes no more messages from HR.");
        }

        var now = clock.UtcNow;
        var message = new HrRequestMessage { Request = record, AuthorUserId = caller.UserId, FromHr = true, Text = text!, CreatedAt = now };
        record.Messages.Add(message);
        if (askForInfo)
        {
            record.AskForInfo(caller.UserId, now);
        }
        else
        {
            record.UpdatedAt = now;
            db.RequestAlerts.Add(new HrRequestAlert { RequestId = record.Id, RecipientUserId = record.SubmittedBy, CreatedAt = now });
        }

        return await SaveAsync(record, rowVersion!, db, users, log, caller.UserId, cancellationToken);
    }

    internal static async Task<Results<Ok<HrRequestView>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        HrRequestStatus outcome, int requestId, DecisionRequest request, HttpContext httpContext, HrDbContext db, IUserDirectory users, CompanyClock clock, HrLog log, CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = Input.RequiredText(request.Note, HrRequestConfiguration.NoteMaxLength, errors, "note", "The decision note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var record = await Load(db).SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);
        if (record is null)
        {
            return HrProblems.RequestNotFound();
        }

        // Nobody decides their own request, whatever their HR rights; the employee the request is about is its submitter.
        if (record.SubmittedBy == caller.UserId || record.Employee.UserId == caller.UserId)
        {
            log.RequestRefused(caller.UserId, outcome.ToString(), record.Id, "own request");
            return HrProblems.ReviewerIsSubmitter();
        }

        if (!record.CanMoveTo(outcome))
        {
            return HrProblems.WrongStatus($"A {record.Status} request can't be {outcome}.");
        }

        record.Decide(outcome, caller.UserId, clock.UtcNow, note!);
        return await SaveAsync(record, rowVersion!, db, users, log, caller.UserId, cancellationToken);
    }

    // ---- Shared -------------------------------------------------------------------------------------------------

    private static async Task<Results<Ok<HrRequestView>, ValidationProblem, ProblemHttpResult>> SaveAsync(
        HrRequest record, byte[] rowVersion, HrDbContext db, IUserDirectory users, HrLog log, int actorUserId, CancellationToken cancellationToken)
    {
        db.Entry(record).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.RequestMoved(actorUserId, record.Id, record.Status.ToString());
        return TypedResults.Ok(await ViewAsync(await Load(db).SingleAsync(candidate => candidate.Id == record.Id, cancellationToken), users, cancellationToken));
    }

    private static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> PageAsync(
        HrDbContext db, IUserDirectory users, IQueryable<HrRequest> query, string? status, string? type, int? employeeId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        HrRequestStatus? wantedStatus = status is null ? null : Input.OptionalName<HrRequestStatus>(status, errors, "status");
        HrRequestType? wantedType = type is null ? null : Input.OptionalName<HrRequestType>(type, errors, "type");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var counts = await query.GroupBy(request => request.Status).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        if (wantedStatus is { } wanted)
        {
            query = query.Where(request => request.Status == wanted);
        }

        if (wantedType is { } kind)
        {
            query = query.Where(request => request.Type == kind);
        }

        if (employeeId is { } employee)
        {
            query = query.Where(request => request.EmployeeId == employee);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Include(request => request.Employee).Include(request => request.Messages).Include(request => request.Attachments)
            .OrderByDescending(request => request.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var people = await users.FindAsync(rows.SelectMany(People).Distinct().ToList(), cancellationToken);
        return TypedResults.Ok(new ListResponse(
            rows.Select(request => View(request, people)).ToList(), page, pageSize, total,
            counts.ToDictionary(row => row.Key.ToString(), row => row.Count)));
    }

    /// <summary>A request with its employee, thread and evidence: tracked, since the same query serves the steps that change it.</summary>
    private static IQueryable<HrRequest> Load(HrDbContext db) =>
        db.Requests.Include(request => request.Employee).Include(request => request.Messages).Include(request => request.Attachments);

    /// <summary>The employee record of the signed-in user, or <c>null</c>: HR self-service is for linked logins only.</summary>
    private static Task<Employee?> MeAsync(HrDbContext db, HrCaller caller, CancellationToken cancellationToken) =>
        db.Employees.AsNoTracking().SingleOrDefaultAsync(employee => employee.UserId == caller.UserId, cancellationToken);

    /// <summary>The attachments named by the form: uploaded by the caller and not bound yet.</summary>
    private static async Task<List<HrRequestAttachment>> AttachmentsAsync(HrDbContext db, IReadOnlyList<int>? ids, int uploaderUserId, ValidationErrors errors, CancellationToken cancellationToken)
    {
        if (ids is not { Count: > 0 })
        {
            return [];
        }

        var wanted = ids.Distinct().ToList();
        var found = await db.RequestAttachments.Where(attachment => wanted.Contains(attachment.Id)).ToListAsync(cancellationToken);
        if (found.Count != wanted.Count || found.Any(attachment => attachment.UploadedBy != uploaderUserId || attachment.RequestId != null))
        {
            errors.Add("attachmentIds", "Every attachment must be one you uploaded and not used on another request.");
        }

        return found;
    }

    /// <summary>Tells HR (every active HR user and Executive, except the actor) that a request waits for them.</summary>
    private static async Task AlertHrAsync(HrDbContext db, IUserDirectory users, HrRequest record, int actorUserId, DateTime now, CancellationToken cancellationToken)
    {
        var recipients = await users.ListActiveAsync([AccessRoles.Hr, AccessRoles.Executive], cancellationToken);
        var already = record.Id == 0 ? [] : await db.RequestAlerts.Where(alert => alert.RequestId == record.Id).Select(alert => alert.RecipientUserId).ToListAsync(cancellationToken);
        foreach (var user in recipients.Where(user => user.Id != actorUserId && !already.Contains(user.Id)))
        {
            db.RequestAlerts.Add(new HrRequestAlert { Request = record, RecipientUserId = user.Id, CreatedAt = now });
        }
    }

    private static IEnumerable<int> People(HrRequest request) =>
        new[] { (int?)request.SubmittedBy, request.ReviewedBy, request.DecidedBy }.OfType<int>().Concat(request.Messages.Select(message => message.AuthorUserId)).Concat(request.Attachments.Select(attachment => attachment.UploadedBy));

    private static async Task<HrRequestView> ViewAsync(HrRequest request, IUserDirectory users, CancellationToken cancellationToken) =>
        View(request, await users.FindAsync(People(request).Distinct().ToList(), cancellationToken));

    private static HrRequestView View(HrRequest request, IReadOnlyDictionary<int, UserSummary> people) => new(
        request.Id, request.Number, request.EmployeeId, request.Employee.EmployeeNo, request.Employee.NameAr, request.Employee.NameEn, request.Type.ToString(),
        request.Subject, request.Details, request.FromDate, request.ToDate, request.ReferralId, request.Status.ToString(), request.IsOpen,
        request.SubmittedAt, HrPersonRef.From(request.SubmittedBy, people), request.ReviewStartedAt, HrPersonRef.From(request.ReviewedBy, people),
        request.DecidedAt, HrPersonRef.From(request.DecidedBy, people), request.DecisionNote,
        request.Messages.OrderBy(message => message.Id).Select(message => new HrRequestMessageView(
            message.Id, message.AuthorUserId, HrPersonRef.From(message.AuthorUserId, people), message.FromHr, message.Text, message.CreatedAt,
            request.Attachments.Where(attachment => attachment.MessageId == message.Id).OrderBy(attachment => attachment.Id).Select(View).ToList())).ToList(),
        request.Attachments.OrderBy(attachment => attachment.Id).Select(View).ToList(),
        RowVersions.Format(request.RowVersion));

    private static HrRequestAttachmentView View(HrRequestAttachment attachment) =>
        new(attachment.Id, attachment.FileName, attachment.ContentType, attachment.SizeBytes, attachment.UploadedAt, attachment.UploadedBy, attachment.MessageId);

    private static string SafeName(string? fileName, PrivateFileTypes.Kind kind)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? "");
        var clean = new string(name.Where(character => !char.IsControl(character) && !Path.GetInvalidFileNameChars().Contains(character)).ToArray()).Trim();
        if (clean.Length == 0)
        {
            clean = "evidence";
        }

        return (clean.Length > 150 ? clean[..150] : clean) + kind.Extension;
    }
}
