using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using AlMotaheda.Modules.Subcontractors.Features.Work;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Payments;

/// <summary>
/// <c>POST /api/projects/{pid}/subcontracts/{cid}/payments</c>: ask to pay the subcontractor under this contract. It
/// creates a Finance payment request (status Submitted) for the Executive; the payment is written here only when the
/// money has left (<c>SubcontractPaymentSource</c>). Then, as before, it settles the contract's oldest approved, unpaid
/// work first, and what is left is an unapplied advance (decision S3).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Needs payments Create and the project in scope. A completed project takes no payment except from an Executive.</item>
///   <item>Payments that are not voided, open requests, and this one must fit in the contract value (409 <c>exceeds_contract_value</c>).</item>
///   <item>Allowed on a completed contract: the final and retention payments come after the work.</item>
///   <item>The same <c>idempotencyKey</c> with the same content returns the first request with 200 and creates
///   nothing; anything different answers 409.</item>
/// </list>
/// </remarks>
internal static class RecordSubPayment
{
    internal sealed record Request(
        string? PaymentDate,
        decimal? Amount,
        string? Method,
        string? Purpose,
        string? Reference,
        string? Notes,
        string? IdempotencyKey);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontracts/{contractId:int}/payments", HandleAsync)
            .RequirePermission(SubPermissions.Payments, PermissionAction.Create)
            .WithName("RecordSubcontractPayment")
            .WithSummary("Record a payment to a subcontractor; it settles the oldest approved work first (idempotent by idempotencyKey)");

    internal static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        IPaymentRequests requests,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
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

        if (!Input.TryParseName<SubPaymentMethod>(request.Method, out var method))
        {
            errors.Add("method", $"method must be one of: {string.Join(", ", Enum.GetNames<SubPaymentMethod>())}.");
        }

        if (!Input.TryParseName<SubPaymentPurpose>(request.Purpose, out var purpose))
        {
            errors.Add("purpose", $"purpose must be one of: {string.Join(", ", Enum.GetNames<SubPaymentPurpose>())}.");
        }

        var reference = Input.OptionalText(request.Reference, SubPaymentConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var notes = Input.OptionalText(request.Notes, SubPaymentConfiguration.NotesMaxLength, errors, "notes", "Notes");

        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var idempotencyKey) || idempotencyKey == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this payment.");
        }

        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return SubcontractorsProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return SubcontractorsProblems.CompletedProject();
        }

        var sent = new PaymentContent(paymentDate!.Value, amount!.Value, method, purpose);

        // A retried request: answer with the payment it already made.
        if (await FindRepeatAsync(requests, projectId, contractId, idempotencyKey, sent, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        var contract = await db.SubContracts.AsNoTracking()
            .Where(candidate => candidate.Id == contractId && candidate.ProjectId == projectId)
            // The value that counts: signed value + approved variations.
            .Select(candidate => new { Value = candidate.Value + (db.ContractVariations.Where(variation => variation.ContractId == candidate.Id && variation.Status == VariationStatus.Approved).Sum(variation => (decimal?)variation.Amount) ?? 0), candidate.SubcontractorId })
            .SingleOrDefaultAsync(cancellationToken);
        if (contract is null)
        {
            return SubcontractorsProblems.ContractNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return SubcontractorsProblems.TryAgainLater();
        }

        // Checked again under the lock: a concurrent retry with the same key may have committed while this request
        // waited, and its payment must be the answer (not an over-value error for money already recorded).
        if (await FindRepeatAsync(requests, projectId, contractId, idempotencyKey, sent, cancellationToken) is { } committed)
        {
            return committed;
        }

        var paid = await db.SubPayments
            .Where(payment => payment.ContractId == contractId && payment.VoidedAt == null)
            .SumAsync(payment => payment.Amount, cancellationToken);

        // Open payment requests on this contract count as paid here, so the contract value can't be requested twice.
        var shared = SharedTransaction.Of(db);
        var reserved = (await requests.ReservedAsync(PaidRecordTypes.SubContract, [contractId], shared, cancellationToken))
            .GetValueOrDefault(contractId);

        if (!WorkRules.FitsContractValue(contract.Value, paid + reserved, amount.Value))
        {
            return SubcontractorsProblems.OverValue(Math.Max(0, contract.Value - paid - reserved));
        }

        PaymentRequestRef created;
        try
        {
            created = await requests.SubmitAsync(
                new NewPaymentRequest(
                    PaymentSourceTypes.SubcontractPayment,
                    PayeeTypes.Subcontractor,
                    contract.SubcontractorId,
                    projectId,
                    purpose == SubPaymentPurpose.Advance ? PaymentPurposes.SubcontractAdvance : PaymentPurposes.SubcontractWork,
                    amount.Value,
                    method.ToString(),
                    paymentDate.Value,
                    reference,
                    notes,
                    idempotencyKey,
                    caller.UserId,
                    [new PaymentLine(PaidRecordTypes.SubContract, contractId, projectId, amount.Value)],
                    SubcontractPayload.Of(purpose)),
                shared,
                cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PaymentRequestIndexes.IdempotencyKey))
        {
            // The same form was sent twice at the same moment: the other request won. Answer with its request.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await FindRepeatAsync(requests, projectId, contractId, idempotencyKey, sent, cancellationToken)
                ?? SubcontractorsProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.PaymentRequested(caller.UserId, projectId, contractId, created.Id);

        return TypedResults.Created($"/api/payment-requests/{created.Id}", created);
    }

    private sealed record PaymentContent(DateOnly PaymentDate, decimal Amount, SubPaymentMethod Method, SubPaymentPurpose Purpose);

    /// <summary>
    /// The answer for a key that was used before: the first payment (200) when it is on this contract and asks for the
    /// same payment; 409 when the key was used on another contract or for a different payment (amount, date, method or
    /// purpose); <c>null</c> when the key is new.
    /// </summary>
    private static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        IPaymentRequests requests, int projectId, int contractId, Guid idempotencyKey, PaymentContent sent, CancellationToken cancellationToken)
    {
        if (await requests.FindByKeyAsync(idempotencyKey, cancellationToken) is not { } existing)
        {
            return null;
        }

        var sameContract = existing.Lines.Count == 1
            && existing.Lines[0].RecordType == PaidRecordTypes.SubContract
            && existing.Lines[0].RecordId == contractId;
        if (existing.SourceType != PaymentSourceTypes.SubcontractPayment || !sameContract || existing.ProjectId != projectId
            || existing.RequestedDate != sent.PaymentDate || existing.Amount != sent.Amount
            || existing.Method != sent.Method.ToString()
            || existing.Purpose != (sent.Purpose == SubPaymentPurpose.Advance ? PaymentPurposes.SubcontractAdvance : PaymentPurposes.SubcontractWork))
        {
            return SubcontractorsProblems.KeyUsedElsewhere();
        }

        return TypedResults.Ok(existing);
    }
}

/// <summary>What a subcontract payment request carries for this module: the payment's own purpose (Finance only knows advance or work).</summary>
internal static class SubcontractPayload
{
    public static string Of(SubPaymentPurpose purpose) => purpose.ToString();

    public static SubPaymentPurpose Read(string? payload) =>
        Enum.TryParse<SubPaymentPurpose>(payload, out var purpose)
            ? purpose
            : throw new PaymentNotPayableException("The request doesn't say what the subcontract payment is for.");
}

internal static class VoidSubPayment
{
    internal sealed record Request(string? Reason, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontract-payments/{paymentId:int}/void", HandleAsync)
            .RequirePermission(SubPermissions.Payments, PermissionAction.Approve)
            .WithName("VoidSubcontractPayment")
            .WithSummary("Void a subcontractor payment (payments Approve): its work is open again and other advances move to it");

    internal static async Task<Results<Ok<SubPaymentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int paymentId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IPaymentRequests requests,
        IProjectAccess access,
        ContractDetails details,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(request.Reason, SubPaymentConfiguration.VoidReasonMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return SubcontractorsProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return SubcontractorsProblems.CompletedProject();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return SubcontractorsProblems.TryAgainLater();
        }

        var payment = await db.SubPayments
            .SingleOrDefaultAsync(candidate => candidate.Id == paymentId && candidate.Contract.ProjectId == projectId, cancellationToken);
        if (payment is null)
        {
            return SubcontractorsProblems.PaymentNotFound();
        }

        if (payment.IsVoided)
        {
            return SubcontractorsProblems.AlreadyVoided();
        }

        var now = clock.UtcNow;
        payment.VoidedAt = now;
        payment.VoidedBy = caller.UserId;
        payment.VoidReason = reason;
        db.Entry(payment).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        // The voided payment's work is open again: other unapplied money of the contract goes to it.
        var allocations = await ContractSettlement.ApplyAsync(db, payment.ContractId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        // The ledger reversal (Finance) commits together with the void.
        await requests.ReverseAsync(PaymentSourceTypes.SubcontractPayment, payment.Id, reason!, caller.UserId, SharedTransaction.Of(db), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.PaymentVoided(caller.UserId, projectId, payment.ContractId, payment.Id, allocations);

        var view = await details.PaymentAsync(projectId, payment.Id, cancellationToken);
        return view is null ? SubcontractorsProblems.PaymentNotFound() : TypedResults.Ok(view);
    }
}
