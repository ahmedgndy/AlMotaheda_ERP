using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Payments;

/// <summary>
/// <c>POST /api/workers/{id}/payments</c>: ask to pay a worker. The server decides which days the money settles: the
/// oldest approved, unpaid days first, across every project (<see cref="PaymentFifo"/>), skipping what open payment
/// requests already hold. It creates a Finance payment request (status Submitted) for the Executive; the payment is
/// written here only when the money has left (<c>WorkerPaymentSource</c>). One transaction under the worker's lock.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Needs payments Create. More than what the worker is owed answers 409 <c>over_payment</c> (no advances, decision W2).</item>
///   <item>The same <c>idempotencyKey</c> with the same content returns the first request with 200 and creates nothing;
///   anything different answers 409 <c>idempotency_key_reused</c>.</item>
///   <item>The request shows only the days on projects the caller may see (QA fix B4).</item>
/// </list>
/// </remarks>
internal static class RecordWorkerPayment
{
    internal sealed record Request(
        string? PaymentDate,
        decimal? Amount,
        string? Method,
        string? Reference,
        string? Notes,
        string? IdempotencyKey);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/workers/{workerId:int}/payments", HandleAsync)
            .RequirePermission(WorkforcePermissions.Payments, PermissionAction.Create)
            .WithName("RecordWorkerPayment")
            .WithSummary("Record a payment to a worker; it settles the oldest approved unpaid days first (idempotent by idempotencyKey)");

    internal static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int workerId,
        Request request,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IPaymentRequests requests,
        CompanyClock clock,
        WorkforceLog log,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var paymentDate = Input.RequiredDate(request.PaymentDate, errors, "paymentDate", "The payment date");
        if (paymentDate > clock.Today)
        {
            errors.Add("paymentDate", "The payment date can't be in the future.");
        }

        var amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");

        if (!Input.TryParseName<WorkerPaymentMethod>(request.Method, out var method))
        {
            errors.Add("method", $"method must be one of: {string.Join(", ", Enum.GetNames<WorkerPaymentMethod>())}.");
        }

        var reference = Input.OptionalText(request.Reference, WorkerPaymentConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var notes = Input.OptionalText(request.Notes, WorkerPaymentConfiguration.NotesMaxLength, errors, "notes", "Notes");

        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var idempotencyKey) || idempotencyKey == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this payment.");
        }

        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        var sent = new PaymentContent(paymentDate!.Value, amount!.Value, method);

        // A retried request: answer with the payment it already made.
        if (await FindRepeatAsync(requests, access, httpContext.User, workerId, idempotencyKey, sent, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        if (!await db.Workers.AnyAsync(worker => worker.Id == workerId, cancellationToken))
        {
            return WorkforceProblems.WorkerNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Worker(workerId), cancellationToken))
        {
            return WorkforceProblems.TryAgainLater();
        }

        // Checked again under the lock: a concurrent retry with the same key may have committed while this request
        // waited, and its payment must be the answer (not an over-payment error for pay already settled).
        if (await FindRepeatAsync(requests, access, httpContext.User, workerId, idempotencyKey, sent, cancellationToken) is { } committed)
        {
            return committed;
        }

        var payable = await WorkerPaymentQueries.PayableLines(db, workerId).ToListAsync(cancellationToken);
        var paidParts = await WorkerPaymentQueries.PaidParts(db, workerId).ToListAsync(cancellationToken);

        // Days held by open payment requests count as paid for this plan, so no day is requested twice.
        var shared = SharedTransaction.Of(db);
        var reserved = await requests.ReservedAsync(
            PaidRecordTypes.AttendanceLine, payable.Select(line => line.LineId).ToList(), shared, cancellationToken);
        var held = reserved.Select(entry => new PaidPart(entry.Key, entry.Value, PaymentVoided: false));

        // Days held by pending weekly settlements (cash already paid on site, waiting for review) are taken too.
        var settling = await Settlements.SettlementQueries.HeldParts(db, payable.Select(line => line.LineId).ToList()).ToListAsync(cancellationToken);
        held = held.Concat(settling);

        var plan = PaymentFifo.Plan(amount.Value, PaymentFifo.Settle(payable, paidParts.Concat(held)));
        if (!plan.Fits)
        {
            // The outstanding pay is named only when every project it comes from is visible to the caller.
            var payableProjects = payable.Select(line => line.ProjectId).Distinct().ToList();
            var visible = await ProjectVisibility.AmongAsync(access, httpContext.User, payableProjects, cancellationToken);
            return WorkforceProblems.PaysTooMuch(visible.Count == payableProjects.Count ? plan.Outstanding : null);
        }

        var projectOf = payable.ToDictionary(line => line.LineId, line => line.ProjectId);
        var lines = plan.Allocations
            .Select(line => new PaymentLine(PaidRecordTypes.AttendanceLine, line.LineId, projectOf[line.LineId], line.Amount))
            .ToList();
        var projects = lines.Select(line => line.ProjectId).Distinct().ToList();

        PaymentRequestRef created;
        try
        {
            created = await requests.SubmitAsync(
                new NewPaymentRequest(
                    PaymentSourceTypes.WorkerPayment,
                    PayeeTypes.Worker,
                    workerId,
                    projects.Count == 1 ? projects[0] : null,
                    PaymentPurposes.WorkerWages,
                    amount.Value,
                    method.ToString(),
                    paymentDate!.Value,
                    reference,
                    notes,
                    idempotencyKey,
                    caller.UserId,
                    lines),
                shared,
                cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PaymentRequestIndexes.IdempotencyKey))
        {
            // The same form was sent twice at the same moment: the other request won. Answer with its request.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await FindRepeatAsync(requests, access, httpContext.User, workerId, idempotencyKey, sent, cancellationToken)
                ?? WorkforceProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.PaymentRequested(caller.UserId, workerId, created.Id, lines.Count);

        return TypedResults.Created($"/api/payment-requests/{created.Id}", await VisibleLinesAsync(access, httpContext.User, created, cancellationToken));
    }

    private sealed record PaymentContent(DateOnly PaymentDate, decimal Amount, WorkerPaymentMethod Method);

    /// <summary>
    /// The answer for a key that was used before: the first payment (200) when it is for this worker and asks for the
    /// same payment; 409 <c>idempotency_key_reused</c> when the key was used for another worker or for a different
    /// payment (amount, date or method); <c>null</c> when the key is new.
    /// </summary>
    private static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        IPaymentRequests requests, IProjectAccess access, System.Security.Claims.ClaimsPrincipal caller, int workerId, Guid idempotencyKey,
        PaymentContent sent, CancellationToken cancellationToken)
    {
        if (await requests.FindByKeyAsync(idempotencyKey, cancellationToken) is not { } existing)
        {
            return null;
        }

        if (existing.SourceType != PaymentSourceTypes.WorkerPayment
            || existing.PayeeId != workerId
            || existing.RequestedDate != sent.PaymentDate
            || existing.Amount != sent.Amount
            || existing.Method != sent.Method.ToString())
        {
            return WorkforceProblems.KeyUsedElsewhere();
        }

        return TypedResults.Ok(await VisibleLinesAsync(access, caller, existing, cancellationToken));
    }

    /// <summary>The request with only the days on projects the caller may see.</summary>
    private static async Task<PaymentRequestRef> VisibleLinesAsync(
        IProjectAccess access, System.Security.Claims.ClaimsPrincipal caller, PaymentRequestRef request, CancellationToken cancellationToken)
    {
        var visible = await ProjectVisibility.AmongAsync(access, caller, request.Lines.Select(line => line.ProjectId).Distinct().ToList(), cancellationToken);
        return request with { Lines = request.Lines.Where(line => visible.Contains(line.ProjectId)).ToList() };
    }
}
