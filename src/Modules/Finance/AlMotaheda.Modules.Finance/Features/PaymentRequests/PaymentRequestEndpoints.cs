using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Data.Configurations;
using AlMotaheda.Modules.Finance.Requests;
using AlMotaheda.Modules.Finance.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Features.PaymentRequests;

/// <summary>
/// <c>GET /api/payment-requests</c>: the requests the caller may see, newest first, with the count per status (the
/// Executive's queue is <c>status=Submitted</c>). Amounts only with payments See money.
/// </summary>
internal static class ListPaymentRequests
{
    internal sealed record Response(
        IReadOnlyList<PaymentRequestRow> Items, int Page, int PageSize, int TotalCount, IReadOnlyDictionary<string, int> CountsByStatus);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/payment-requests", HandleAsync)
            .RequirePermission(FinancePermissions.Payments, PermissionAction.View)
            .WithName(nameof(ListPaymentRequests))
            .WithSummary("List payment requests (status, payeeType, payeeId, projectId, sourceType; page, pageSize) with counts per status");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        FinanceDbContext db,
        IVisibleProjects projects,
        PaymentRequestViews views,
        CancellationToken cancellationToken,
        string? status = null,
        string? payeeType = null,
        int? payeeId = null,
        int? projectId = null,
        string? sourceType = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var wantedStatus = Input.OptionalName<PaymentRequestStatus>(status, errors, "status");
        var wantedPayeeType = Input.Clean(payeeType);
        if (wantedPayeeType is not null && !PayeeTypes.All.Contains(wantedPayeeType))
        {
            errors.Add("payeeType", $"payeeType must be one of: {string.Join(", ", PayeeTypes.All)}.");
        }

        var wantedSourceType = Input.Clean(sourceType);
        if (wantedSourceType is not null && !PaymentSourceTypes.All.Contains(wantedSourceType))
        {
            errors.Add("sourceType", $"sourceType must be one of: {string.Join(", ", PaymentSourceTypes.All)}.");
        }

        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var visible = await caller.VisibleProjectsAsync(projects, cancellationToken);
        var filter = new RequestFilter(wantedStatus, wantedPayeeType, payeeId, projectId, wantedSourceType, visible);

        var query = PaymentRequestViews.Query(db, filter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var counts = await PaymentRequestViews.Query(db, filter with { Status = null })
            .GroupBy(request => request.Status)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(
            await views.RowsAsync(rows, caller.SeesAmounts, cancellationToken),
            page,
            pageSize,
            totalCount,
            counts.ToDictionary(entry => entry.Key.ToString(), entry => entry.Count)));
    }
}

/// <summary><c>GET /api/payment-requests/{id}</c>: one request with its lines and its approval history.</summary>
internal static class GetPaymentRequest
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/payment-requests/{requestId:int}", HandleAsync)
            .RequirePermission(FinancePermissions.Payments, PermissionAction.View)
            .WithName(nameof(GetPaymentRequest))
            .WithSummary("Get a payment request with its lines and history");

    internal static async Task<Results<Ok<PaymentRequestDetails>, ProblemHttpResult>> HandleAsync(
        int requestId, HttpContext httpContext, FinanceDbContext db, IVisibleProjects projects, PaymentRequestViews views,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var visible = await caller.VisibleProjectsAsync(projects, cancellationToken);
        var projectId = await db.PaymentRequests.AsNoTracking()
            .Where(request => request.Id == requestId)
            .Select(request => new { request.ProjectId })
            .SingleOrDefaultAsync(cancellationToken);
        if (projectId is null || !FinanceCaller.Sees(visible, projectId.ProjectId))
        {
            return FinanceProblems.RequestNotFound();
        }

        var details = await views.DetailsAsync(requestId, caller.SeesAmounts, cancellationToken);
        return details is null ? FinanceProblems.RequestNotFound() : TypedResults.Ok(details);
    }
}

/// <summary>
/// The steps of a request. Deciding (approve, reject, approve and pay) needs payments Approve (the Executive by
/// default), never by the request's creator. The bank step and the confirmation need finance Edit (the Accountant).
/// Cancelling is for the creator or an Executive. Every step re-checks the caller's permission in the database.
/// </summary>
internal static class MovePaymentRequest
{
    internal sealed record DecisionRequest(string? Note, string? RowVersion);

    internal sealed record PayRequest(string? Note, string? PaidOn, string? Reference, string? RowVersion);

    internal sealed record ReasonRequest(string? Reason, string? RowVersion);

    internal sealed record BankRequest(string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        var requests = api.MapGroup("/payment-requests/{requestId:int}");

        requests.MapPost("/approve", ApproveAsync)
            .RequirePermission(FinancePermissions.Payments, PermissionAction.Approve)
            .WithName("ApprovePaymentRequest")
            .WithSummary("Approve a submitted payment request (payments Approve; not its creator)");

        requests.MapPost("/reject", RejectAsync)
            .RequirePermission(FinancePermissions.Payments, PermissionAction.Approve)
            .WithName("RejectPaymentRequest")
            .WithSummary("Reject a submitted payment request with a note (payments Approve; not its creator). Final");

        requests.MapPost("/approve-and-pay", ApproveAndPayAsync)
            .RequirePermission(FinancePermissions.Payments, PermissionAction.Approve)
            .WithName("ApproveAndPayPaymentRequest")
            .WithSummary("Approve and pay: cash is paid at once (paidOn, reference), a bank transfer or cheque goes to the bank");

        requests.MapPost("/send-to-bank", SendToBankAsync)
            .RequirePermission(FinancePermissions.Finance, PermissionAction.Edit)
            .WithName("SendPaymentRequestToBank")
            .WithSummary("Send an approved (or failed) bank transfer or cheque to the bank (finance Edit)");

        requests.MapPost("/confirm-payment", ConfirmPaymentAsync)
            .RequirePermission(FinancePermissions.Finance, PermissionAction.Edit)
            .WithName("ConfirmPaymentRequestPaid")
            .WithSummary("Confirm the money left (finance Edit): writes the payment and the ledger. Cash after approval, bank after sending");

        requests.MapPost("/bank-failure", RecordFailureAsync)
            .RequirePermission(FinancePermissions.Finance, PermissionAction.Edit)
            .WithName("RecordPaymentRequestBankFailure")
            .WithSummary("Record that the bank did not pay (finance Edit). The request can be sent again or cancelled");

        requests.MapPost("/cancel", CancelAsync)
            .RequirePermission(FinancePermissions.Payments, PermissionAction.View)
            .WithName("CancelPaymentRequest")
            .WithSummary("Cancel an unpaid request with a reason (its creator or an Executive). Frees what it reserved");
    }

    internal static Task<Results<Ok<PaymentRequestDetails>, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        int requestId, DecisionRequest body, HttpContext httpContext, PaymentRequestWorkflow workflow, CompanyClock clock,
        CancellationToken cancellationToken) =>
        DecideAsync(requestId, body, httpContext, workflow, noteRequired: false, "Approve",
            (request, caller, note, _) => Task.FromResult(StepOutcome.From(request.Approve(caller.UserId, clock.UtcNow, note), request)),
            cancellationToken);

    internal static Task<Results<Ok<PaymentRequestDetails>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        int requestId, DecisionRequest body, HttpContext httpContext, PaymentRequestWorkflow workflow, CompanyClock clock,
        CancellationToken cancellationToken) =>
        DecideAsync(requestId, body, httpContext, workflow, noteRequired: true, "Reject",
            (request, caller, note, _) => Task.FromResult(StepOutcome.From(request.Reject(caller.UserId, clock.UtcNow, note!), request)),
            cancellationToken);

    internal static async Task<Results<Ok<PaymentRequestDetails>, ValidationProblem, ProblemHttpResult>> ApproveAndPayAsync(
        int requestId, PayRequest body, HttpContext httpContext, PaymentRequestWorkflow workflow, CompanyClock clock,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = Input.OptionalText(body.Note, PaymentRequestConfiguration.NoteMaxLength, errors, "note", "The note");
        var paidOn = PaidOn(body.PaidOn, clock, errors);
        var reference = Input.OptionalText(body.Reference, PaymentRequestConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        return await workflow.RunAsync(requestId, rowVersion!, caller, "ApproveAndPay", (FinancePermissions.Payments, PermissionAction.Approve),
            async (request, token) =>
            {
                if (PaymentMethods.GoesThroughBank(request.Method))
                {
                    return await workflow.SendToBankAsync(request, caller.UserId, alsoApprove: true, note, token);
                }

                // Cash: approved and paid in one step (refactor plan, proposal 4).
                var approved = StepOutcome.From(request.ApproveAndPay(caller.UserId, clock.UtcNow, note, bankReference: null), request);
                return approved.Problem is not null ? approved : await workflow.PayAsync(request, caller.UserId, paidOn!.Value, reference, token);
            },
            cancellationToken);
    }

    internal static async Task<Results<Ok<PaymentRequestDetails>, ValidationProblem, ProblemHttpResult>> SendToBankAsync(
        int requestId, BankRequest body, HttpContext httpContext, PaymentRequestWorkflow workflow, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        return await workflow.RunAsync(requestId, rowVersion!, caller, "SendToBank", (FinancePermissions.Finance, PermissionAction.Edit),
            (request, token) => workflow.SendToBankAsync(request, caller.UserId, alsoApprove: false, note: null, token),
            cancellationToken);
    }

    internal static async Task<Results<Ok<PaymentRequestDetails>, ValidationProblem, ProblemHttpResult>> ConfirmPaymentAsync(
        int requestId, PayRequest body, HttpContext httpContext, PaymentRequestWorkflow workflow, CompanyClock clock,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var paidOn = PaidOn(body.PaidOn, clock, errors);
        var reference = Input.OptionalText(body.Reference, PaymentRequestConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        return await workflow.RunAsync(requestId, rowVersion!, caller, "ConfirmPayment", (FinancePermissions.Finance, PermissionAction.Edit),
            (request, token) => workflow.PayAsync(request, caller.UserId, paidOn!.Value, reference, token),
            cancellationToken);
    }

    internal static async Task<Results<Ok<PaymentRequestDetails>, ValidationProblem, ProblemHttpResult>> RecordFailureAsync(
        int requestId, ReasonRequest body, HttpContext httpContext, PaymentRequestWorkflow workflow, CompanyClock clock,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(body.Reason, PaymentRequestConfiguration.NoteMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        return await workflow.RunAsync(requestId, rowVersion!, caller, "RecordFailure", (FinancePermissions.Finance, PermissionAction.Edit),
            (request, _) => Task.FromResult(StepOutcome.From(request.RecordFailure(caller.UserId, clock.UtcNow, reason!), request)),
            cancellationToken);
    }

    internal static async Task<Results<Ok<PaymentRequestDetails>, ValidationProblem, ProblemHttpResult>> CancelAsync(
        int requestId, ReasonRequest body, HttpContext httpContext, PaymentRequestWorkflow workflow, CompanyClock clock,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(body.Reason, PaymentRequestConfiguration.NoteMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        // Re-checked: still signed in with payments View. Who may cancel (the creator or an Executive) is the step's rule.
        return await workflow.RunAsync(requestId, rowVersion!, caller, "Cancel", (FinancePermissions.Payments, PermissionAction.View),
            (request, _) => Task.FromResult(
                request.CreatedBy != caller.UserId && !caller.IsExecutive
                    ? new StepOutcome(ApiProblem.ForbiddenResult("Only the person who created this request, or an Executive, can cancel it."))
                    : StepOutcome.From(request.Cancel(caller.UserId, clock.UtcNow, reason!), request)),
            cancellationToken);
    }

    private static async Task<Results<Ok<PaymentRequestDetails>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        int requestId,
        DecisionRequest body,
        HttpContext httpContext,
        PaymentRequestWorkflow workflow,
        bool noteRequired,
        string actionName,
        Func<PaymentRequest, FinanceCaller, string?, CancellationToken, Task<StepOutcome>> step,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = noteRequired
            ? Input.RequiredText(body.Note, PaymentRequestConfiguration.NoteMaxLength, errors, "note", "The note")
            : Input.OptionalText(body.Note, PaymentRequestConfiguration.NoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        return await workflow.RunAsync(requestId, rowVersion!, caller, actionName, (FinancePermissions.Payments, PermissionAction.Approve),
            (request, token) => step(request, caller, note, token),
            cancellationToken);
    }

    /// <summary>The paid date: today when not given, never in the future.</summary>
    private static DateOnly? PaidOn(string? value, CompanyClock clock, ValidationErrors errors)
    {
        var paidOn = Input.OptionalDate(value, errors, "paidOn", "The paid date") ?? clock.Today;
        if (paidOn > clock.Today)
        {
            errors.Add("paidOn", "The paid date can't be in the future.");
        }

        return paidOn;
    }
}
