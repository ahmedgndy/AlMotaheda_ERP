using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
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
/// <c>POST /api/workers/{id}/payments</c>: record money paid to a worker. The server decides which days it settles:
/// the oldest approved, unpaid days first, across every project (<see cref="PaymentFifo"/>). One transaction under
/// the worker's lock, so two payments can't both settle the same day.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Needs payments Create. A payment above what the worker is owed answers 409 <c>over_payment</c> (no advances, decision W2).</item>
///   <item>The same <c>idempotencyKey</c> again returns the first payment with 200 and records nothing; the same key
///   for another worker answers 409 <c>idempotency_key_reused</c>.</item>
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

    internal static async Task<Results<Created<WorkerPaymentView>, Ok<WorkerPaymentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int workerId,
        Request request,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
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
        if (await FindRepeatAsync(db, access, httpContext.User, workerId, idempotencyKey, sent, cancellationToken) is { } repeat)
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
        if (await FindRepeatAsync(db, access, httpContext.User, workerId, idempotencyKey, sent, cancellationToken) is { } committed)
        {
            return committed;
        }

        var payable = await WorkerPaymentQueries.PayableLines(db, workerId).ToListAsync(cancellationToken);
        var paidParts = await WorkerPaymentQueries.PaidParts(db, workerId).ToListAsync(cancellationToken);
        var plan = PaymentFifo.Plan(amount.Value, PaymentFifo.Settle(payable, paidParts));
        if (!plan.Fits)
        {
            // The outstanding pay is named only when every project it comes from is visible to the caller.
            var payableProjects = payable.Select(line => line.ProjectId).Distinct().ToList();
            var visible = await ProjectVisibility.AmongAsync(access, httpContext.User, payableProjects, cancellationToken);
            return WorkforceProblems.PaysTooMuch(visible.Count == payableProjects.Count ? plan.Outstanding : null);
        }

        var payment = new WorkerPayment
        {
            WorkerId = workerId,
            PaymentDate = paymentDate!.Value,
            Amount = amount.Value,
            Method = method,
            Reference = reference,
            Notes = notes,
            IdempotencyKey = idempotencyKey,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        payment.Allocations.AddRange(plan.Allocations.Select(line => new WorkerPaymentAllocation { LineId = line.LineId, Amount = line.Amount }));
        db.WorkerPayments.Add(payment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, WorkerPaymentConfiguration.IdempotencyKeyIndex))
        {
            // The same form was sent twice at the same moment: the other request won. Answer with its payment.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await FindRepeatAsync(db, access, httpContext.User, workerId, idempotencyKey, sent, cancellationToken) ?? WorkforceProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.PaymentRecorded(caller.UserId, workerId, payment.Id, payment.Allocations.Count);

        var view = await WorkerPaymentVisibility.ViewForAsync(db, access, httpContext.User, payment.Id, cancellationToken);
        return TypedResults.Created($"/api/workers/{workerId}/payments/{payment.Id}", view);
    }

    private sealed record PaymentContent(DateOnly PaymentDate, decimal Amount, WorkerPaymentMethod Method);

    /// <summary>
    /// The answer for a key that was used before: the first payment (200) when it is for this worker and asks for the
    /// same payment; 409 <c>idempotency_key_reused</c> when the key was used for another worker or for a different
    /// payment (amount, date or method); <c>null</c> when the key is new.
    /// </summary>
    private static async Task<Results<Created<WorkerPaymentView>, Ok<WorkerPaymentView>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        WorkforceDbContext db, IProjectAccess access, System.Security.Claims.ClaimsPrincipal caller, int workerId, Guid idempotencyKey,
        PaymentContent sent, CancellationToken cancellationToken)
    {
        var existing = await WorkerPaymentQueries.ByKey(db, idempotencyKey)
            .Select(payment => new { payment.Id, payment.WorkerId, payment.PaymentDate, payment.Amount, payment.Method })
            .SingleOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return null;
        }

        if (existing.WorkerId != workerId
            || existing.PaymentDate != sent.PaymentDate
            || existing.Amount != sent.Amount
            || existing.Method != sent.Method)
        {
            return WorkforceProblems.KeyUsedElsewhere();
        }

        return TypedResults.Ok(await WorkerPaymentVisibility.ViewForAsync(db, access, caller, existing.Id, cancellationToken));
    }
}
