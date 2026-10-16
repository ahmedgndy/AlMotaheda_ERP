using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>
/// <c>POST /api/projects/{pid}/equipment-costs/{id}/payment</c>: ask to pay an approved credit equipment cost, for its
/// full amount. It creates a Finance payment request (status Submitted) for the Executive; the cost is marked paid
/// only when the money has left (<c>EquipmentCostPaymentSource</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Needs payments Create and the project in scope. A custody cost takes no payment (it is paid at approval).</item>
///   <item>409 <c>already_paid</c> when paid, 409 <c>payment_requested</c> while an open request covers the cost.</item>
///   <item>The form's <c>rowVersion</c> must be the cost's current one. Sending the same form again (same cost and row
///   version, same details) returns the first request with 200; the key is made from the cost and its row version.</item>
/// </list>
/// </remarks>
internal static class RecordEquipmentCostPayment
{
    internal sealed record Request(string? PaidOn, string? Method, string? Reference, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment-costs/{costId:int}/payment", HandleAsync)
            .RequirePermission(FleetPermissions.PaymentsModule, PermissionAction.Create)
            .WithName("RecordEquipmentCostPayment")
            .WithSummary("Request the payment of an approved credit equipment cost, for its full amount (payments Create). 409 already_paid, payment_requested, invalid_status");

    internal static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int costId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IPaymentRequests requests,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var paidOn = Input.RequiredDate(request.PaidOn, errors, "paidOn", "The payment date");
        if (paidOn > clock.Today)
        {
            errors.Add("paidOn", "The payment date can't be in the future.");
        }

        var method = Input.RequiredName<PaymentMethod>(request.Method, errors, "method");
        var reference = Input.OptionalText(request.Reference, EquipmentCostConfiguration.PaymentReferenceMaxLength, errors, "reference", "The reference");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return FleetProblems.CompletedProject();
        }

        var key = PaymentKey(costId, rowVersion!);
        var sent = new PaymentContent(costId, paidOn!.Value, method!.Value);
        if (await FindRepeatAsync(requests, key, sent, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        if (await FindRepeatAsync(requests, key, sent, cancellationToken) is { } committed)
        {
            return committed;
        }

        var cost = await db.EquipmentCosts.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == costId && candidate.ProjectId == projectId, cancellationToken);
        if (cost is null)
        {
            return FleetProblems.CostNotFound();
        }

        // Nothing is saved on the cost now, so its row version is compared here: the form must show the current cost.
        if (!cost.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        switch (EquipmentCostRules.Pay(cost.Status, cost.Channel, cost.PaidOn is not null))
        {
            case CostDecision.WrongStatus:
                return FleetProblems.WrongStatus(cost.Channel == CostChannel.Custody
                    ? "A custody cost is paid on site and counts as paid when approved. It takes no payment."
                    : $"Only an approved cost can be paid. This one is {cost.Status}.");
            case CostDecision.AlreadyPaid:
                return FleetProblems.PaymentAlreadyRecorded();
        }

        var shared = SharedTransaction.Of(db);
        if ((await requests.ReservedAsync(PaidRecordTypes.EquipmentCost, [costId], shared, cancellationToken)).ContainsKey(costId))
        {
            return FleetProblems.PaymentAlreadyRequested();
        }

        PaymentRequestRef created;
        try
        {
            created = await requests.SubmitAsync(
                new NewPaymentRequest(
                    PaymentSourceTypes.EquipmentCostPayment,
                    PayeeTypes.Supplier,
                    cost.SupplierId!.Value,
                    projectId,
                    PaymentPurposes.EquipmentCost,
                    cost.Amount,
                    method.Value.ToString(),
                    paidOn.Value,
                    reference,
                    null,
                    key,
                    caller.UserId,
                    [new PaymentLine(PaidRecordTypes.EquipmentCost, costId, projectId, cost.Amount)]),
                shared,
                cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PaymentRequestIndexes.IdempotencyKey))
        {
            // The same form was sent twice at the same moment: the other request won. Answer with its request.
            await transaction.RollbackAsync(cancellationToken);
            return await FindRepeatAsync(requests, key, sent, cancellationToken) ?? FleetProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.CostPaymentRequested(caller.UserId, projectId, costId, created.Id);

        return TypedResults.Created($"/api/payment-requests/{created.Id}", created);
    }

    private sealed record PaymentContent(int CostId, DateOnly PaidOn, PaymentMethod Method);

    /// <summary>
    /// The idempotency key of a payment form: the same cost at the same row version is the same form, so a retry gets
    /// the same key and the first request back.
    /// </summary>
    internal static Guid PaymentKey(int costId, byte[] rowVersion)
    {
        var hash = System.Security.Cryptography.SHA256.HashData([.. BitConverter.GetBytes(costId), .. rowVersion, .. "EquipmentCostPayment"u8]);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        IPaymentRequests requests, Guid key, PaymentContent sent, CancellationToken cancellationToken)
    {
        if (await requests.FindByKeyAsync(key, cancellationToken) is not { } existing)
        {
            return null;
        }

        var sameCost = existing.Lines.Count == 1 && existing.Lines[0].RecordType == PaidRecordTypes.EquipmentCost && existing.Lines[0].RecordId == sent.CostId;
        if (existing.SourceType != PaymentSourceTypes.EquipmentCostPayment || !sameCost
            || existing.RequestedDate != sent.PaidOn || existing.Method != sent.Method.ToString())
        {
            return FleetProblems.KeyUsedElsewhere();
        }

        return TypedResults.Ok(existing);
    }
}

/// <summary>
/// <c>POST /api/projects/{pid}/equipment-costs/{id}/payment/void</c>: remove a wrongly recorded payment. Needs
/// payments Approve. The cost is unpaid again, so the right payment can be recorded; who voided it, when and why
/// stay on the cost.
/// </summary>
/// <remarks>
/// Payments are never edited or deleted (overview section 3.4), so before the payment columns are emptied the
/// whole payment is copied, with the void, to a <see cref="VoidedEquipmentCostPayment"/> row. A later pay and void
/// then adds a second row and never overwrites the first one.
/// </remarks>
internal static class VoidEquipmentCostPayment
{
    internal sealed record Request(string? Reason, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment-costs/{costId:int}/payment/void", HandleAsync)
            .RequirePermission(FleetPermissions.PaymentsModule, PermissionAction.Approve)
            .WithName("VoidEquipmentCostPayment")
            .WithSummary("Void the payment of an equipment cost (payments Approve): the cost is unpaid again. 409 not_paid");

    internal static async Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int costId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IPaymentRequests requests,
        IProjectAccess access,
        IUserDirectory users,
        ISupplierDirectory suppliers,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(request.Reason, EquipmentCostConfiguration.VoidReasonMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return FleetProblems.CompletedProject();
        }

        // The void and its ledger reversal (Finance) commit together.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var cost = await db.EquipmentCosts
            .SingleOrDefaultAsync(candidate => candidate.Id == costId && candidate.ProjectId == projectId, cancellationToken);
        if (cost is null)
        {
            return FleetProblems.CostNotFound();
        }

        if (EquipmentCostRules.VoidPayment(cost.PaidOn is not null) == CostDecision.NotPaid)
        {
            return FleetProblems.NoPayment();
        }

        var now = clock.UtcNow;
        db.VoidedEquipmentCostPayments.Add(new VoidedEquipmentCostPayment
        {
            CostId = cost.Id,
            PaidOn = cost.PaidOn!.Value,
            PaymentMethod = cost.PaymentMethod!.Value,
            PaymentReference = cost.PaymentReference,
            PaymentRecordedAt = cost.PaymentRecordedAt!.Value,
            PaymentRecordedBy = cost.PaymentRecordedBy!.Value,
            VoidedAt = now,
            VoidedBy = caller.UserId,
            VoidReason = reason!,
        });

        cost.PaidOn = null;
        cost.PaymentMethod = null;
        cost.PaymentReference = null;
        cost.PaymentRecordedAt = null;
        cost.PaymentRecordedBy = null;
        cost.PaymentVoidedAt = now;
        cost.PaymentVoidedBy = caller.UserId;
        cost.PaymentVoidReason = reason;
        db.Entry(cost).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await requests.ReverseAsync(PaymentSourceTypes.EquipmentCostPayment, cost.Id, reason!, caller.UserId, SharedTransaction.Of(db), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.CostPaymentVoided(caller.UserId, projectId, cost.Id);

        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, cost.Id, cancellationToken);
        return view is null ? FleetProblems.CostNotFound() : TypedResults.Ok(view);
    }
}
