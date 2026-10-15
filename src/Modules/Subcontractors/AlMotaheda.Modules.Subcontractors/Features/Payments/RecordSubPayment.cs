using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
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
/// <c>POST /api/projects/{pid}/subcontracts/{cid}/payments</c>: record money paid to the subcontractor under this
/// contract. The server decides what it settles: the contract's oldest approved, unpaid work first; what is left
/// is an unapplied advance (decision S3).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Needs payments Create and the project in scope. A completed project takes no payment except from an Executive.</item>
///   <item>Payments that are not voided, plus this one, must fit in the contract value (409 <c>exceeds_contract_value</c>).</item>
///   <item>Allowed on a completed contract: the final and retention payments come after the work.</item>
///   <item>The same <c>idempotencyKey</c> again returns the first payment with 200 and records nothing; the same
///   key on another contract answers 409.</item>
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

    internal static async Task<Results<Created<SubPaymentView>, Ok<SubPaymentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
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

        // A retried request: answer with the payment it already made.
        if (await FindRepeatAsync(db, details, projectId, contractId, idempotencyKey, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        var contractValue = await db.SubContracts.AsNoTracking()
            .Where(contract => contract.Id == contractId && contract.ProjectId == projectId)
            .Select(contract => (decimal?)contract.Value)
            .SingleOrDefaultAsync(cancellationToken);
        if (contractValue is null)
        {
            return SubcontractorsProblems.ContractNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return SubcontractorsProblems.TryAgainLater();
        }

        var paid = await db.SubPayments
            .Where(payment => payment.ContractId == contractId && payment.VoidedAt == null)
            .SumAsync(payment => payment.Amount, cancellationToken);

        if (!WorkRules.FitsContractValue(contractValue.Value, paid, amount!.Value))
        {
            return SubcontractorsProblems.OverValue(Math.Max(0, contractValue.Value - paid));
        }

        var now = clock.UtcNow;
        var payment = new SubPayment
        {
            ContractId = contractId,
            PaymentDate = paymentDate!.Value,
            Amount = amount.Value,
            Method = method,
            Purpose = purpose,
            Reference = reference,
            Notes = notes,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        db.SubPayments.Add(payment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SubPaymentConfiguration.IdempotencyKeyIndex))
        {
            // The same form was sent twice at the same moment: the other request won. Answer with its payment.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await FindRepeatAsync(db, details, projectId, contractId, idempotencyKey, cancellationToken)
                ?? SubcontractorsProblems.KeyUsedElsewhere();
        }

        // Saved first, so the settlement sees the payment with its id and the oldest-first order is exact.
        var allocations = await ContractSettlement.ApplyAsync(db, contractId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.PaymentRecorded(caller.UserId, projectId, contractId, payment.Id, allocations);

        var view = await details.PaymentAsync(projectId, payment.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/subcontract-payments/{payment.Id}", view!);
    }

    /// <summary>
    /// The answer for a key that was used before: the first payment (200) when it is on this contract, 409 when the
    /// key was used on another contract; <c>null</c> when the key is new.
    /// </summary>
    private static async Task<Results<Created<SubPaymentView>, Ok<SubPaymentView>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        SubcontractorsDbContext db, ContractDetails details, int projectId, int contractId, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        var existing = await ContractQueries.ByKey(db, idempotencyKey)
            .Select(payment => new { payment.Id, payment.ContractId, payment.Contract.ProjectId })
            .SingleOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return null;
        }

        if (existing.ContractId != contractId || existing.ProjectId != projectId)
        {
            return SubcontractorsProblems.KeyUsedElsewhere();
        }

        var view = await details.PaymentAsync(projectId, existing.Id, cancellationToken);
        return TypedResults.Ok(view!);
    }
}

/// <summary>
/// <c>POST /api/projects/{pid}/subcontract-payments/{id}/void</c>: cancel a wrong payment. The row stays, marked
/// with who voided it, when and why. Its allocations stop counting, so the work it settled is open again, and the
/// other unapplied money of the contract is applied to that work at once.
/// </summary>
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
        await transaction.CommitAsync(cancellationToken);

        log.PaymentVoided(caller.UserId, projectId, payment.ContractId, payment.Id, allocations);

        var view = await details.PaymentAsync(projectId, payment.Id, cancellationToken);
        return view is null ? SubcontractorsProblems.PaymentNotFound() : TypedResults.Ok(view);
    }
}
