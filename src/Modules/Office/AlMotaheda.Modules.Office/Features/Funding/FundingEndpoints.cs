using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Data.Configurations;
using AlMotaheda.Modules.Office.Features.Alerts;
using AlMotaheda.Modules.Office.Features.Attachments;
using AlMotaheda.Modules.Office.Features.Expenses;
using AlMotaheda.Modules.Office.Features.Ledger;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Features.Funding;

internal sealed record OfficeFundingActions(bool Approve, bool Reject, bool Cancel, bool ConfirmReceipt);

internal sealed record OfficeFundingView(
    int Id,
    int CustodyId,
    string CustodyNameAr,
    string CustodyNameEn,
    decimal RequestedAmount,
    string Reason,
    DateOnly RequestedDate,
    string Status,
    OfficePersonRef? RequestedBy,
    DateTime RequestedAt,
    decimal? ApprovedAmount,
    OfficePersonRef? ReviewedBy,
    DateTime? ReviewedAt,
    string? ReviewNote,
    int? PaymentRequestId,
    string? PaymentRequestNumber,
    string? PaymentRequestStatus,
    string? PaymentMethod,
    decimal? DisbursedAmount,
    DateOnly? DisbursedOn,
    string? DisbursementReference,
    decimal? ReceivedAmount,
    DateTime? ReceivedAt,
    OfficePersonRef? ReceivedBy,
    string? ReceiptNote,
    IReadOnlyList<OfficeAttachmentView> Attachments,
    OfficeFundingActions Actions,
    string RowVersion);

/// <summary>
/// Custody funding ("طلب تغذية العهدة"). Approving one never adds money: it asks Finance to pay (a payment request
/// for the Executive). When Finance marks it paid, the request is Disbursed; the balance grows only when the
/// custodian confirms receiving the cash, by the amount they received, once.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>POST /api/office/funding-requests</c> (office Create): the custodian, or a reviewer for any custody.</item>
///   <item><c>…/approve</c> (office Approve, not the requester): full or partial amount and the payment method.</item>
///   <item><c>…/reject</c>, <c>…/cancel</c>: cancel while waiting, or after Finance rejected or cancelled its request.</item>
///   <item><c>…/confirm-receipt</c> (the custodian): posts the funding entry once; less than disbursed alerts the reviewers.</item>
/// </list>
/// </remarks>
internal static class OfficeFundingEndpoints
{
    internal sealed record CreateRequest(int? CustodyId, decimal? Amount, string? Reason, string? RequestedDate, IReadOnlyList<int>? AttachmentIds, Guid? IdempotencyKey);

    internal sealed record ApproveRequest(decimal? ApprovedAmount, string? Method, string? Note, string? RowVersion);

    internal sealed record ReceiptRequest(decimal? ReceivedAmount, string? Note, IReadOnlyList<int>? AttachmentIds, string? RowVersion);

    internal sealed record FundingRow(
        int Id, int CustodyId, decimal RequestedAmount, decimal? ApprovedAmount, decimal? DisbursedAmount, decimal? ReceivedAmount, string Status,
        string Reason, DateOnly RequestedDate, DateTime RequestedAt, int RequestedBy);

    internal sealed record FundingList(IReadOnlyList<FundingRow> Items, int Total, int Page, int PageSize);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/office/funding-requests", ListAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("ListOfficeFundingRequests")
            .WithSummary("Custody funding requests the caller may see (custodyId, status, page, pageSize)");

        api.MapGet("/office/funding-requests/{fundingId:int}", GetAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeFundingRequest")
            .WithSummary("One funding request, with its Finance payment request's status");

        api.MapPost("/office/funding-requests", CreateAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Create)
            .WithName("CreateOfficeFundingRequest")
            .WithSummary("Ask for cash for a custody (office Create)");

        api.MapPost("/office/funding-requests/{fundingId:int}/approve", ApproveAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .RequirePermission(OfficePermissions.Payments, PermissionAction.Create)
            .WithName("ApproveOfficeFundingRequest")
            .WithSummary("Agree a funding request (fully or partly) and ask Finance to pay it (office Approve)");

        api.MapPost("/office/funding-requests/{fundingId:int}/reject", RejectAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("RejectOfficeFundingRequest")
            .WithSummary("Reject a funding request (office Approve)");

        api.MapPost("/office/funding-requests/{fundingId:int}/cancel", CancelAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("CancelOfficeFundingRequest")
            .WithSummary("Cancel a funding request not paid out (its requester or a reviewer)");

        api.MapPost("/office/funding-requests/{fundingId:int}/confirm-receipt", ConfirmReceiptAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Create)
            .WithName("ConfirmOfficeFundsReceived")
            .WithSummary("The custodian confirms the cash they received; posts it to the custody once");
    }

    internal static async Task<Results<Ok<FundingList>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, OfficeDbContext db, CancellationToken cancellationToken,
        int? custodyId = null, string? status = null, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var statusFilter = Input.OptionalName<OfficeFundingStatus>(status, errors, "status");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var query = Visible(db, caller);
        if (custodyId is { } custody)
        {
            query = query.Where(funding => funding.CustodyId == custody);
        }

        if (statusFilter is { } wanted)
        {
            query = query.Where(funding => funding.Status == wanted);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(funding => funding.RequestedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(funding => new FundingRow(funding.Id, funding.CustodyId, funding.RequestedAmount, funding.ApprovedAmount, funding.DisbursedAmount,
                funding.ReceivedAmount, funding.Status.ToString(), funding.Reason, funding.RequestedDate, funding.RequestedAt, funding.RequestedBy))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new FundingList(rows, total, page, pageSize));
    }

    internal static async Task<Results<Ok<OfficeFundingView>, ProblemHttpResult>> GetAsync(
        int fundingId, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IPaymentRequests payments, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        return await ViewAsync(db, users, payments, caller, fundingId, cancellationToken) is { } view ? TypedResults.Ok(view) : OfficeProblems.FundingNotFound();
    }

    internal static async Task<Results<Created<OfficeFundingView>, Ok<OfficeFundingView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPaymentRequests payments,
        OfficeAlerts alerts,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.CustodyId is null)
        {
            errors.Add("custodyId", "Choose the custody.");
        }

        var amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");
        var reason = Input.RequiredText(request.Reason, OfficeLengths.Note, errors, "reason", "The reason");
        var date = Input.OptionalDate(request.RequestedDate, errors, "requestedDate", "The date") ?? clock.Today;
        var key = OfficeInput.Key(request.IdempotencyKey, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        if (await FindRepeatAsync(db, users, payments, caller, key!.Value, request.CustodyId!.Value, amount!.Value, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var custody = await db.Custodies.SingleOrDefaultAsync(candidate => candidate.Id == request.CustodyId, cancellationToken);
        if (custody is null || !caller.MaySee(custody))
        {
            return OfficeProblems.CustodyNotFound();
        }

        if (!caller.Holds(custody) && !caller.IsReviewer)
        {
            return OfficeProblems.NotTheHolder();
        }

        if (!custody.IsActive)
        {
            return OfficeProblems.Inactive(custody.Status.ToString());
        }

        var (attachments, refused) = await AttachmentBinding.LoadAsync(db, (request.AttachmentIds ?? []).Distinct().ToList(), caller.UserId,
            OfficeAttachmentOwners.FundingRequest, null, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var funding = new OfficeFundingRequest
        {
            CustodyId = custody.Id,
            RequestedAmount = amount.Value,
            Reason = reason!,
            RequestedDate = date,
            IdempotencyKey = key.Value,
            RequestedBy = caller.UserId,
            RequestedAt = clock.UtcNow,
        };
        db.FundingRequests.Add(funding);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeFundingRequestConfiguration.KeyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await FindRepeatAsync(db, users, payments, caller, key.Value, custody.Id, amount.Value, cancellationToken) ?? OfficeProblems.KeyUsedElsewhere();
        }

        AttachmentBinding.Bind(attachments, OfficeAttachmentOwners.FundingRequest, funding.Id, clock.UtcNow);
        alerts.Add<OfficeFundingRequestedAlert>(db, await alerts.ReviewersAsync(caller.UserId, cancellationToken), custody.Id,
            OfficeAttachmentOwners.FundingRequest, funding.Id, clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.FundingMoved(caller.UserId, funding.Id, funding.Status.ToString());

        return TypedResults.Created($"/api/office/funding-requests/{funding.Id}", (await ViewAsync(db, users, payments, caller, funding.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeFundingView>, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        int fundingId,
        ApproveRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPaymentRequests payments,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var approvedAmount = NumberInput.OptionalMoney(request.ApprovedAmount, errors, "approvedAmount", "The approved amount");
        var method = Input.Clean(request.Method) ?? PaymentMethods.Cash;
        if (!PaymentMethods.All.Contains(method))
        {
            errors.Add("method", $"method must be one of: {string.Join(", ", PaymentMethods.All)}.");
        }

        var note = Input.OptionalText(request.Note, OfficeLengths.Note, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (funding, custody, refusal) = await LoadForReviewAsync(db, caller, fundingId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        // A double tap: the first approval already asked Finance.
        if (funding!.Status == OfficeFundingStatus.Approved && funding.ReviewedBy == caller.UserId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TypedResults.Ok((await ViewAsync(db, users, payments, caller, fundingId, cancellationToken))!);
        }

        if (!funding.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (funding.Status != OfficeFundingStatus.Submitted)
        {
            return OfficeProblems.WrongStatus($"Only a waiting funding request can be approved. This one is {funding.Status}.");
        }

        if (!custody!.IsActive)
        {
            return OfficeProblems.Inactive(custody.Status.ToString());
        }

        // Finance never lets the creator of a payment request decide it, and only the Executive decides payments:
        // the Accountant agrees the funding here, the Executive approves its payment in Finance.
        if (caller.IsExecutive)
        {
            log.ActionRefused(caller.UserId, nameof(ApproveAsync), "the Executive decides the payment in Finance");
            return OfficeProblems.FinanceSeparationRequired();
        }

        var amount = approvedAmount ?? funding.RequestedAmount;
        if (amount > funding.RequestedAmount)
        {
            errors.Add("approvedAmount", "The approved amount can't be more than the requested amount.");
            return OfficeProblems.Validation(errors);
        }

        // The payment request's key comes from the funding request, so a retried approval can't ask Finance twice.
        var key = PaymentKey(funding.Id);
        funding.MoveTo(OfficeFundingStatus.Approved);
        funding.ApprovedAmount = amount;
        funding.ReviewedBy = caller.UserId;
        funding.ReviewedAt = clock.UtcNow;
        funding.ReviewNote = note;
        funding.PaymentMethod = method;
        funding.PaymentRequestKey = key;

        PaymentRequestRef created;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            created = await payments.SubmitAsync(
                new NewPaymentRequest(
                    PaymentSourceTypes.OfficeCustodyFunding,
                    PayeeTypes.OfficeCustody,
                    funding.Id,
                    null,
                    PaymentPurposes.OfficeCustodyAdvance,
                    amount,
                    method,
                    clock.Today,
                    null,
                    $"Office custody funding {funding.Id} — {custody.NameEn}",
                    key,
                    caller.UserId,
                    []),
                SharedTransaction.Of(db),
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PaymentRequestIndexes.IdempotencyKey))
        {
            return OfficeProblems.WrongStatus("Finance was already asked to pay this funding request.");
        }

        funding.PaymentRequestId = created.Id;
        funding.PaymentRequestNumber = created.Number;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.FundingMoved(caller.UserId, funding.Id, funding.Status.ToString());
        log.FundingPaymentRequested(caller.UserId, funding.Id, created.Id);

        return TypedResults.Ok((await ViewAsync(db, users, payments, caller, funding.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeFundingView>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        int fundingId,
        OfficeExpenseEndpoints.DecisionRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPaymentRequests payments,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = OfficeInput.Reason(request.Note, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (funding, _, refusal) = await LoadForReviewAsync(db, caller, fundingId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (funding!.Status == OfficeFundingStatus.Rejected)
        {
            return TypedResults.Ok((await ViewAsync(db, users, payments, caller, fundingId, cancellationToken))!);
        }

        if (!funding.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (funding.Status != OfficeFundingStatus.Submitted)
        {
            return OfficeProblems.WrongStatus($"Only a waiting funding request can be rejected. This one is {funding.Status}.");
        }

        funding.MoveTo(OfficeFundingStatus.Rejected);
        funding.ReviewedBy = caller.UserId;
        funding.ReviewedAt = clock.UtcNow;
        funding.ReviewNote = note;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.FundingMoved(caller.UserId, funding.Id, funding.Status.ToString());
        return TypedResults.Ok((await ViewAsync(db, users, payments, caller, funding.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeFundingView>, ValidationProblem, ProblemHttpResult>> CancelAsync(
        int fundingId,
        OfficeExpenseEndpoints.DecisionRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPaymentRequests payments,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var funding = await db.FundingRequests.SingleOrDefaultAsync(candidate => candidate.Id == fundingId, cancellationToken);
        var custody = funding is null ? null : await db.Custodies.AsNoTracking().SingleAsync(candidate => candidate.Id == funding.CustodyId, cancellationToken);
        if (funding is null || custody is null || !caller.MaySee(custody))
        {
            return OfficeProblems.FundingNotFound();
        }

        if (funding.RequestedBy != caller.UserId && !caller.IsReviewer)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (funding.Status == OfficeFundingStatus.Cancelled)
        {
            return TypedResults.Ok((await ViewAsync(db, users, payments, caller, fundingId, cancellationToken))!);
        }

        if (!funding.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        var cancellable = funding.Status == OfficeFundingStatus.Submitted
                          || (funding.Status == OfficeFundingStatus.Approved && await FinanceDroppedAsync(payments, funding, cancellationToken));
        if (!cancellable)
        {
            return OfficeProblems.WrongStatus(funding.Status == OfficeFundingStatus.Approved
                ? "Finance is still handling the payment. Ask Finance to reject or cancel its payment request first."
                : $"A {funding.Status} funding request can't be cancelled.");
        }

        funding.MoveTo(OfficeFundingStatus.Cancelled);
        funding.CancelledAt = clock.UtcNow;
        funding.CancelledBy = caller.UserId;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.FundingMoved(caller.UserId, funding.Id, funding.Status.ToString());
        return TypedResults.Ok((await ViewAsync(db, users, payments, caller, funding.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeFundingView>, ValidationProblem, ProblemHttpResult>> ConfirmReceiptAsync(
        int fundingId,
        ReceiptRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPaymentRequests payments,
        IPriceControl priceControl,
        OfficeAlerts alerts,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var received = NumberInput.OptionalMoney(request.ReceivedAmount, errors, "receivedAmount", "The received amount");
        var note = Input.OptionalText(request.Note, OfficeLengths.Note, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var custodyId = await db.FundingRequests.Where(funding => funding.Id == fundingId).Select(funding => (int?)funding.CustodyId).SingleOrDefaultAsync(cancellationToken);
        if (custodyId is null)
        {
            return OfficeProblems.FundingNotFound();
        }

        if (!await CustodyLedger.LockAsync(db, custodyId.Value, cancellationToken))
        {
            return OfficeProblems.TryAgainLater();
        }

        var funding = await db.FundingRequests.SingleAsync(candidate => candidate.Id == fundingId, cancellationToken);
        var custody = await db.Custodies.SingleAsync(candidate => candidate.Id == custodyId, cancellationToken);
        if (!caller.MaySee(custody))
        {
            return OfficeProblems.FundingNotFound();
        }

        if (!caller.Holds(custody))
        {
            return OfficeProblems.NotTheHolder();
        }

        // A retried confirmation: the money is already in the ledger once.
        if (funding.Status == OfficeFundingStatus.Received)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TypedResults.Ok((await ViewAsync(db, users, payments, caller, fundingId, cancellationToken))!);
        }

        if (!funding.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (funding.Status != OfficeFundingStatus.Disbursed)
        {
            return OfficeProblems.WrongStatus(funding.Status == OfficeFundingStatus.Approved
                ? "Finance has not paid this out yet. Confirm when you have the cash."
                : $"A {funding.Status} funding request can't be received.");
        }

        var amount = received ?? funding.DisbursedAmount!.Value;
        if (amount > funding.DisbursedAmount)
        {
            errors.Add("receivedAmount", $"You can't receive more than Finance paid out ({funding.DisbursedAmount:0.00}).");
            return OfficeProblems.Validation(errors);
        }

        if (amount < funding.DisbursedAmount && note is null)
        {
            errors.Add("note", "Explain why you received less than Finance paid out.");
            return OfficeProblems.Validation(errors);
        }

        var (attachments, refused) = await AttachmentBinding.LoadAsync(db, (request.AttachmentIds ?? []).Distinct().ToList(), caller.UserId,
            OfficeAttachmentOwners.FundingRequest, funding.Id, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var now = clock.UtcNow;
        funding.MoveTo(OfficeFundingStatus.Received);
        funding.ReceivedAmount = amount;
        funding.ReceivedAt = now;
        funding.ReceivedBy = caller.UserId;
        funding.ReceiptNote = note;
        AttachmentBinding.Bind(attachments, OfficeAttachmentOwners.FundingRequest, funding.Id, now);

        var entry = CustodyLedger.Add(db, custody.Id, OfficeLedgerKind.Funding, amount, clock.Today, OfficeLedgerSources.FundingRequest, funding.Id,
            caller.UserId, now, note, funding.PaymentRequestNumber);
        if (amount < funding.DisbursedAmount)
        {
            alerts.Add<OfficeDiscrepancyAlert>(db, await alerts.ReviewersAsync(caller.UserId, cancellationToken), custody.Id,
                OfficeAttachmentOwners.FundingRequest, funding.Id, now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeLedgerEntryConfiguration.SourceIndex))
        {
            return OfficeProblems.WrongStatus("These funds are already in the custody.");
        }

        log.FundingMoved(caller.UserId, funding.Id, funding.Status.ToString());
        log.EntryPosted(caller.UserId, custody.Id, entry.Id, entry.Kind.ToString());

        // Approved expenses that waited for cash are posted now, oldest first.
        var waiting = await db.Expenses
            .Where(expense => expense.CustodyId == custody.Id && expense.Status == OfficeExpenseStatus.Approved && expense.PostingStatus == OfficePostingStatus.NotPosted)
            .ToListAsync(cancellationToken);
        if (waiting.Count > 0)
        {
            var cleared = await OfficeExpenseEndpoints.PriceClearedAsync(priceControl, waiting, cancellationToken);
            await CustodyLedger.PostReadyAsync(db, custody, waiting, cleared.Contains, caller.UserId, now, alerts, log, cancellationToken);
        }

        await CustodyLedger.CheckLowBalanceAsync(db, custody, await CustodyLedger.RecordedAsync(db, custody.Id, cancellationToken), alerts, log, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ViewAsync(db, users, payments, caller, funding.Id, cancellationToken))!);
    }

    /// <summary>The Finance payment request's key of a funding request: one per funding request, whatever the retries.</summary>
    internal static Guid PaymentKey(int fundingId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData([.. BitConverter.GetBytes(fundingId), .. "OfficeCustodyFunding"u8]);
        return new Guid(hash.AsSpan(0, 16));
    }

    internal static IQueryable<OfficeFundingRequest> Visible(OfficeDbContext db, OfficeCaller caller) =>
        caller.SeesAll
            ? db.FundingRequests.AsNoTracking()
            : db.FundingRequests.AsNoTracking().Where(funding => db.Custodies.Any(custody => custody.Id == funding.CustodyId && custody.HolderUserId == caller.UserId));

    internal static async Task<OfficeFundingView?> ViewAsync(
        OfficeDbContext db, IUserDirectory users, IPaymentRequests payments, OfficeCaller caller, int fundingId, CancellationToken cancellationToken)
    {
        var funding = await Visible(db, caller).SingleOrDefaultAsync(candidate => candidate.Id == fundingId, cancellationToken);
        if (funding is null)
        {
            return null;
        }

        var custody = await db.Custodies.AsNoTracking().SingleAsync(candidate => candidate.Id == funding.CustodyId, cancellationToken);
        var payment = funding.PaymentRequestKey is { } key ? await payments.FindByKeyAsync(key, cancellationToken) : null;
        var people = await users.FindAsync(
            new int?[] { funding.RequestedBy, funding.ReviewedBy, funding.ReceivedBy }.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList(),
            cancellationToken);
        var attachments = await OfficeAttachmentView.OfAsync(db, OfficeAttachmentOwners.FundingRequest, funding.Id, cancellationToken);
        var reviewer = caller.IsReviewer && funding.RequestedBy != caller.UserId && custody.HolderUserId != caller.UserId;
        var financeDropped = payment?.Status is "Rejected" or "Cancelled";

        return new OfficeFundingView(
            funding.Id, funding.CustodyId, custody.NameAr, custody.NameEn, funding.RequestedAmount, funding.Reason, funding.RequestedDate,
            funding.Status.ToString(), OfficePersonRef.From(funding.RequestedBy, people), funding.RequestedAt, funding.ApprovedAmount,
            OfficePersonRef.From(funding.ReviewedBy, people), funding.ReviewedAt, funding.ReviewNote, funding.PaymentRequestId, funding.PaymentRequestNumber,
            payment?.Status, funding.PaymentMethod, funding.DisbursedAmount, funding.DisbursedOn, funding.DisbursementReference, funding.ReceivedAmount,
            funding.ReceivedAt, OfficePersonRef.From(funding.ReceivedBy, people), funding.ReceiptNote, attachments,
            new OfficeFundingActions(
                reviewer && funding.Status == OfficeFundingStatus.Submitted,
                reviewer && funding.Status == OfficeFundingStatus.Submitted,
                (funding.RequestedBy == caller.UserId || caller.IsReviewer)
                && (funding.Status == OfficeFundingStatus.Submitted || (funding.Status == OfficeFundingStatus.Approved && financeDropped)),
                custody.HolderUserId == caller.UserId && funding.Status == OfficeFundingStatus.Disbursed),
            RowVersions.Format(funding.RowVersion));
    }

    private static async Task<bool> FinanceDroppedAsync(IPaymentRequests payments, OfficeFundingRequest funding, CancellationToken cancellationToken) =>
        funding.PaymentRequestKey is { } key && await payments.FindByKeyAsync(key, cancellationToken) is { Status: "Rejected" or "Cancelled" };

    private static async Task<(OfficeFundingRequest? Funding, OfficeCustody? Custody, ProblemHttpResult? Refused)> LoadForReviewAsync(
        OfficeDbContext db, OfficeCaller caller, int fundingId, CancellationToken cancellationToken)
    {
        var funding = await db.FundingRequests.SingleOrDefaultAsync(candidate => candidate.Id == fundingId, cancellationToken);
        if (funding is null)
        {
            return (null, null, OfficeProblems.FundingNotFound());
        }

        var custody = await db.Custodies.SingleAsync(candidate => candidate.Id == funding.CustodyId, cancellationToken);
        if (funding.RequestedBy == caller.UserId || custody.HolderUserId == caller.UserId)
        {
            return (null, null, OfficeProblems.ReviewerIsSubmitter());
        }

        return (funding, custody, null);
    }

    private static async Task<Results<Created<OfficeFundingView>, Ok<OfficeFundingView>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        OfficeDbContext db, IUserDirectory users, IPaymentRequests payments, OfficeCaller caller, Guid key, int custodyId, decimal amount,
        CancellationToken cancellationToken)
    {
        var existing = await db.FundingRequests.AsNoTracking().SingleOrDefaultAsync(funding => funding.IdempotencyKey == key, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.RequestedBy != caller.UserId || existing.CustodyId != custodyId || existing.RequestedAmount != amount)
        {
            return OfficeProblems.KeyUsedElsewhere();
        }

        return TypedResults.Ok((await ViewAsync(db, users, payments, caller, existing.Id, cancellationToken))!);
    }
}
