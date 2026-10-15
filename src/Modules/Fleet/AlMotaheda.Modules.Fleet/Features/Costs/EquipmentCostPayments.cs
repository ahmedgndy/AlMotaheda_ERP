using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>
/// <c>POST /api/projects/{pid}/equipment-costs/{id}/payment</c>: record that an approved credit cost was paid, in
/// one payment for the full amount (decision F2). Needs payments Create.
/// </summary>
/// <remarks>
/// A custody cost was paid on site and counts as paid at approval, so it takes no payment (409
/// <c>invalid_status</c>), and neither does a cost that is not approved. A second payment answers 409
/// <c>already_paid</c>: the <c>rowVersion</c> of the last read makes a retried request harmless.
/// </remarks>
internal static class RecordEquipmentCostPayment
{
    internal sealed record Request(string? PaidOn, string? Method, string? Reference, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment-costs/{costId:int}/payment", HandleAsync)
            .RequirePermission(FleetPermissions.PaymentsModule, PermissionAction.Create)
            .WithName("RecordEquipmentCostPayment")
            .WithSummary("Record the payment of an approved credit equipment cost, for its full amount (payments Create). 409 already_paid, invalid_status");

    internal static async Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int costId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
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

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        var cost = await db.EquipmentCosts
            .SingleOrDefaultAsync(candidate => candidate.Id == costId && candidate.ProjectId == projectId, cancellationToken);
        if (cost is null)
        {
            return FleetProblems.CostNotFound();
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

        cost.PaidOn = paidOn;
        cost.PaymentMethod = method;
        cost.PaymentReference = reference;
        cost.PaymentRecordedAt = clock.UtcNow;
        cost.PaymentRecordedBy = caller.UserId;
        db.Entry(cost).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.CostPaid(caller.UserId, projectId, cost.Id, method!.Value.ToString());

        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, cost.Id, cancellationToken);
        return view is null ? FleetProblems.CostNotFound() : TypedResults.Ok(view);
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

        log.CostPaymentVoided(caller.UserId, projectId, cost.Id);

        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, cost.Id, cancellationToken);
        return view is null ? FleetProblems.CostNotFound() : TypedResults.Ok(view);
    }
}
