using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
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
/// <c>POST /api/projects/{pid}/equipment-costs/{id}/adjustments</c>: ask to correct an approved equipment cost by a
/// signed amount, with a reason (refactor plan M6). The cost stays frozen; an Executive decides.
/// </summary>
/// <remarks>
/// Needs equipment Create or Edit (whoever may record costs) and the project in scope. A credit cost is paid once, in full (decision F2), so it can't be
/// corrected once its payment is requested or made (409 <c>cost_being_paid</c>). One open correction at a time.
/// </remarks>
internal static class RequestEquipmentCostAdjustment
{
    internal sealed record Request(decimal? Amount, string? Reason);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment-costs/{costId:int}/adjustments", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName(nameof(RequestEquipmentCostAdjustment))
            .WithSummary("Ask to correct an approved equipment cost by a signed amount, with a reason; an Executive decides");

    internal static async Task<Results<Created<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int costId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IPaymentRequests payments,
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

        // Whoever may record equipment costs (Create or Edit) may ask to correct one.
        if (!caller.CanRecordCosts)
        {
            return FleetProblems.RecordingNeedsCreateOrEdit();
        }

        var errors = new ValidationErrors();
        var amount = request.Amount;
        if (amount is null or 0)
        {
            errors.Add("amount", "The amount is required and can't be zero: negative lowers the cost, positive raises it.");
        }
        else if (Math.Abs(amount.Value) > NumberInput.MaxMoney || decimal.Round(amount.Value, 2) != amount.Value)
        {
            errors.Add("amount", $"The amount must have at most two decimals and be at most {NumberInput.MaxMoney:0.00} either way.");
        }

        var reason = Input.RequiredText(request.Reason, EquipmentCostAdjustmentConfiguration.ReasonMaxLength, errors, "reason", "The reason");
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

        if (await CostAdjustmentChecks.CheckAsync(db, payments, projectId, costId, amount!.Value, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        if (await db.EquipmentCostAdjustments.AnyAsync(
                adjustment => adjustment.CostId == costId && adjustment.Status == AdjustmentStatus.Requested, cancellationToken))
        {
            return FleetProblems.AdjustmentAlreadyOpen();
        }

        var adjustment = new EquipmentCostAdjustment
        {
            CostId = costId,
            Amount = amount.Value,
            Reason = reason!,
            RequestedAt = clock.UtcNow,
            RequestedBy = caller.UserId,
        };
        db.EquipmentCostAdjustments.Add(adjustment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, EquipmentCostAdjustmentConfiguration.OneOpenIndex))
        {
            return FleetProblems.AdjustmentAlreadyOpen();
        }

        await transaction.CommitAsync(cancellationToken);
        log.CostAdjustmentRequested(caller.UserId, projectId, costId, adjustment.Id);

        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, costId, cancellationToken);
        return view is null ? FleetProblems.CostNotFound() : TypedResults.Created($"/api/projects/{projectId}/equipment-costs/{costId}", view);
    }
}

/// <summary>
/// <c>POST /api/projects/{pid}/equipment-cost-adjustments/{id}/approve</c> and <c>…/reject</c>: an Executive decides
/// a correction (never its requester), re-checked in the database; approval re-checks the cost under the project lock.
/// </summary>
internal static class DecideEquipmentCostAdjustment
{
    internal sealed record Request(string? Note, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapPost("/projects/{projectId:int}/equipment-cost-adjustments/{adjustmentId:int}/approve", ApproveAsync)
            .RequireRole(AccessRoles.Executive)
            .WithName("ApproveEquipmentCostAdjustment")
            .WithSummary("Approve a correction of an equipment cost (Executive, not its requester): every figure then uses the corrected amount");

        api.MapPost("/projects/{projectId:int}/equipment-cost-adjustments/{adjustmentId:int}/reject", RejectAsync)
            .RequireRole(AccessRoles.Executive)
            .WithName("RejectEquipmentCostAdjustment")
            .WithSummary("Reject a correction of an equipment cost with a note (Executive, not its requester)");
    }

    internal static Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        int projectId, int adjustmentId, Request request, HttpContext httpContext, FleetDbContext db, IProjectAccess access,
        IPaymentRequests payments, IExecutiveCheck executives, IUserDirectory users, ISupplierDirectory suppliers, CompanyClock clock,
        FleetLog log, CancellationToken cancellationToken) =>
        DecideAsync(AdjustmentStatus.Approved, projectId, adjustmentId, request, httpContext, db, access, payments, executives, users, suppliers,
            clock, log, cancellationToken);

    internal static Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        int projectId, int adjustmentId, Request request, HttpContext httpContext, FleetDbContext db, IProjectAccess access,
        IPaymentRequests payments, IExecutiveCheck executives, IUserDirectory users, ISupplierDirectory suppliers, CompanyClock clock,
        FleetLog log, CancellationToken cancellationToken) =>
        DecideAsync(AdjustmentStatus.Rejected, projectId, adjustmentId, request, httpContext, db, access, payments, executives, users, suppliers,
            clock, log, cancellationToken);

    private static async Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        AdjustmentStatus outcome,
        int projectId,
        int adjustmentId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IPaymentRequests payments,
        IExecutiveCheck executives,
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
        var note = outcome == AdjustmentStatus.Rejected
            ? Input.RequiredText(request.Note, EquipmentCostConfiguration.ReviewNoteMaxLength, errors, "note", "The note")
            : Input.OptionalText(request.Note, EquipmentCostConfiguration.ReviewNoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        // The token's role can be 15 minutes old: the Executive is re-checked in the database.
        if (httpContext.User.GetSessionId() is not { } session
            || !await executives.IsActiveExecutiveAsync(caller.UserId, session, cancellationToken))
        {
            log.ActionRefused(caller.UserId, $"Decide{nameof(EquipmentCostAdjustment)}", "not a currently signed-in active Executive");
            return FleetProblems.ExecutiveOnly();
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        var adjustment = await db.EquipmentCostAdjustments.SingleOrDefaultAsync(
            candidate => candidate.Id == adjustmentId && db.EquipmentCosts.Any(cost => cost.Id == candidate.CostId && cost.ProjectId == projectId),
            cancellationToken);
        if (adjustment is null)
        {
            return FleetProblems.AdjustmentNotFound();
        }

        if (adjustment.RequestedBy == caller.UserId)
        {
            log.ActionRefused(caller.UserId, $"Decide{nameof(EquipmentCostAdjustment)}", "deciding own adjustment");
            return FleetProblems.ReviewerIsRecorder();
        }

        if (adjustment.Status != AdjustmentStatus.Requested)
        {
            return FleetProblems.WrongStatus($"This correction was already decided: {adjustment.Status}.");
        }

        if (outcome == AdjustmentStatus.Approved
            && await CostAdjustmentChecks.CheckAsync(db, payments, projectId, adjustment.CostId, adjustment.Amount, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        adjustment.Decide(outcome, caller.UserId, clock.UtcNow, note);
        db.Entry(adjustment).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.CostAdjustmentDecided(caller.UserId, projectId, adjustment.CostId, adjustment.Id, outcome.ToString());

        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, adjustment.CostId, cancellationToken);
        return view is null ? FleetProblems.CostNotFound() : TypedResults.Ok(view);
    }
}

/// <summary>The checks a correction shares between asking and approving.</summary>
internal static class CostAdjustmentChecks
{
    /// <summary>Returns the problem to answer with, or <c>null</c>. Runs inside the caller's transaction, under the project lock.</summary>
    public static async Task<ProblemHttpResult?> CheckAsync(
        FleetDbContext db, IPaymentRequests payments, int projectId, int costId, decimal change, CancellationToken cancellationToken)
    {
        var row = await EquipmentCostQueries.SettlementRow(db, projectId, costId).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return FleetProblems.CostNotFound();
        }

        if (row.Status != EquipmentCostStatus.Approved)
        {
            return FleetProblems.WrongStatus($"Only an approved cost is corrected by an adjustment. This one is {row.Status}; edit it instead.");
        }

        if (row.Channel == CostChannel.Credit
            && (row.PaymentRecorded
                || (await payments.ReservedAsync(PaidRecordTypes.EquipmentCost, [costId], SharedTransaction.Of(db), cancellationToken)).ContainsKey(costId)))
        {
            return FleetProblems.BeingPaid();
        }

        return row.Amount + change >= 0 ? null : FleetProblems.AdjustmentBelowZero();
    }
}
