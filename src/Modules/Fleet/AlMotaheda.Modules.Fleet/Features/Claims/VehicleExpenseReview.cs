using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.HR.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Features.Claims;

/// <summary>
/// The review of a claim (fuel Approve; never the submitter or the driver; an Executive for escalated claims and
/// claims above <c>Fleet:ClaimExecutiveApprovalAbove</c>): <c>approve</c> posts it to the vehicle's custody exactly
/// once, <c>reject</c> keeps the reason and releases the exposure, <c>request-evidence</c> sends it back for more
/// photos, <c>escalate</c> hands it to an Executive, <c>reverse</c> undoes a posting with a reason, and
/// <c>refer-to-hr</c> opens an HR referral for a rejected claim. Every step runs under the vehicle's lock with the
/// claim's row version, so two reviewers can't both land.
/// </summary>
internal static class VehicleExpenseReview
{
    internal sealed record ReviewRequest(string? Note, string? RowVersion);

    internal sealed record ReasonRequest(string? Reason, string? RowVersion);

    internal sealed record ReferRequest(string? Subject, string? Details, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        var claims = api.MapGroup("/vehicle-expenses/{claimId:int}");

        claims.MapPost("/approve", ApproveAsync)
            .RequirePermission(FleetPermissions.Fuel, PermissionAction.Approve)
            .WithName("ApproveVehicleExpense")
            .WithSummary("Approve a claim (fuel Approve; not the submitter or the driver): posted to the vehicle's custody once. 409 price_exception_pending, already_posted");

        claims.MapPost("/reject", RejectAsync)
            .RequirePermission(FleetPermissions.Fuel, PermissionAction.Approve)
            .WithName("RejectVehicleExpense")
            .WithSummary("Reject a claim with a reason (fuel Approve): the exposure is released; the claim and its evidence stay");

        claims.MapPost("/request-evidence", RequestEvidenceAsync)
            .RequirePermission(FleetPermissions.Fuel, PermissionAction.Approve)
            .WithName("RequestVehicleExpenseEvidence")
            .WithSummary("Ask the submitter for more evidence (fuel Approve, a note)");

        claims.MapPost("/escalate", EscalateAsync)
            .RequirePermission(FleetPermissions.Fuel, PermissionAction.Approve)
            .WithName("EscalateVehicleExpense")
            .WithSummary("Escalate a claim to an Executive (fuel Approve, a note)");

        claims.MapPost("/reverse", ReverseAsync)
            .RequirePermission(FleetPermissions.Fuel, PermissionAction.Approve)
            .WithName("ReverseVehicleExpense")
            .WithSummary("Reverse a posted claim with a reason (fuel Approve; not the submitter): the custody entry is released, the claim and the posting history stay");

        claims.MapPost("/refer-to-hr", ReferAsync)
            .RequireAnyPermission((FleetPermissions.Fuel, PermissionAction.Approve), (FleetPermissions.Fleet, PermissionAction.Approve))
            .WithName("ReferVehicleExpenseToHr")
            .WithSummary("Refer a rejected claim to HR for investigation (fuel or fleet Approve). HR decides on its own whether anything is deducted. 409 already_referred");
    }

    internal static Task<Results<Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        int claimId, ReviewRequest body, HttpContext httpContext, FleetDbContext db, VehicleExpenseViews views, IEmployeeDirectory employees, IExecutiveCheck executives,
        IPriceControl priceControl, IOptions<FleetOptions> settings, CompanyClock clock, FleetLog log, CancellationToken cancellationToken) =>
        ReviewAsync(VehicleExpenseStatus.Approved, claimId, body, httpContext, db, views, employees, executives, priceControl, settings, clock, log, cancellationToken);

    internal static Task<Results<Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        int claimId, ReviewRequest body, HttpContext httpContext, FleetDbContext db, VehicleExpenseViews views, IEmployeeDirectory employees, IExecutiveCheck executives,
        IPriceControl priceControl, IOptions<FleetOptions> settings, CompanyClock clock, FleetLog log, CancellationToken cancellationToken) =>
        ReviewAsync(VehicleExpenseStatus.Rejected, claimId, body, httpContext, db, views, employees, executives, priceControl, settings, clock, log, cancellationToken);

    internal static Task<Results<Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> RequestEvidenceAsync(
        int claimId, ReviewRequest body, HttpContext httpContext, FleetDbContext db, VehicleExpenseViews views, IEmployeeDirectory employees, IExecutiveCheck executives,
        IPriceControl priceControl, IOptions<FleetOptions> settings, CompanyClock clock, FleetLog log, CancellationToken cancellationToken) =>
        ReviewAsync(VehicleExpenseStatus.EvidenceRequested, claimId, body, httpContext, db, views, employees, executives, priceControl, settings, clock, log, cancellationToken);

    internal static Task<Results<Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> EscalateAsync(
        int claimId, ReviewRequest body, HttpContext httpContext, FleetDbContext db, VehicleExpenseViews views, IEmployeeDirectory employees, IExecutiveCheck executives,
        IPriceControl priceControl, IOptions<FleetOptions> settings, CompanyClock clock, FleetLog log, CancellationToken cancellationToken) =>
        ReviewAsync(VehicleExpenseStatus.Escalated, claimId, body, httpContext, db, views, employees, executives, priceControl, settings, clock, log, cancellationToken);

    private static async Task<Results<Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> ReviewAsync(
        VehicleExpenseStatus outcome,
        int claimId,
        ReviewRequest body,
        HttpContext httpContext,
        FleetDbContext db,
        VehicleExpenseViews views,
        IEmployeeDirectory employees,
        IExecutiveCheck executives,
        IPriceControl priceControl,
        IOptions<FleetOptions> settings,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = outcome == VehicleExpenseStatus.Approved
            ? Input.OptionalText(body.Note, VehicleExpenseConfiguration.NoteMaxLength, errors, "note", "The note")
            : Input.RequiredText(body.Note, VehicleExpenseConfiguration.NoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        var claim = await db.VehicleExpenses.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == claimId, cancellationToken);
        if (claim is null)
        {
            return FleetProblems.ClaimNotFound();
        }

        // An escalated claim, or one above the policy's limit, needs an Executive who is one in the database right now.
        if (claim.Status == VehicleExpenseStatus.Escalated && outcome == VehicleExpenseStatus.Escalated)
        {
            return FleetProblems.WrongStatus("This claim is already escalated.");
        }

        var needsExecutive = claim.Status == VehicleExpenseStatus.Escalated || (settings.Value.ClaimExecutiveApprovalAbove > 0 && claim.Amount > settings.Value.ClaimExecutiveApprovalAbove);
        var isExecutive = caller.IsExecutive && httpContext.User.GetSessionId() is { } session && await executives.IsActiveExecutiveAsync(caller.UserId, session, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Equipment(claim.VehicleId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        var tracked = await db.VehicleExpenses.Include(candidate => candidate.Flags).SingleAsync(candidate => candidate.Id == claimId, cancellationToken);
        switch (VehicleExpenseRules.Review(tracked, caller.UserId, me?.Id, isExecutive, settings.Value.ClaimExecutiveApprovalAbove))
        {
            case ClaimDecision.OwnRecord:
                log.ActionRefused(caller.UserId, $"{outcome}VehicleExpense", "reviewing own claim");
                return FleetProblems.ReviewerIsRecorder();
            case ClaimDecision.WrongStatus:
                return FleetProblems.WrongStatus($"Only a claim waiting for review can be decided. This one is {tracked.Status}.");
            case ClaimDecision.NeedsExecutive:
                log.ActionRefused(caller.UserId, $"{outcome}VehicleExpense", "executive required");
                return FleetProblems.NeedsExecutive();
        }

        _ = needsExecutive;
        var now = clock.UtcNow;
        if (outcome == VehicleExpenseStatus.Approved)
        {
            // The Price Control gate, as on every other path: a pending or rejected case blocks approval.
            if (tracked.PriceExceptionId is not null)
            {
                var states = await priceControl.StatesAsync(PriceExceptionSources.VehicleExpense, [tracked.Id], cancellationToken);
                if (states.TryGetValue(tracked.Id, out var state) && state.IsOpen)
                {
                    return state.Status == "Rejected" ? FleetProblems.PriceCaseRejected() : FleetProblems.PriceCasePending();
                }
            }

            // Posted exactly once: the Pending custody entry becomes Posted; a claim already posted is refused.
            var entry = await db.VehicleCustodyEntries.SingleOrDefaultAsync(candidate => candidate.SourceType == VehicleCustodySources.Expense && candidate.SourceId == tracked.Id, cancellationToken);
            if (tracked.PostingStatus != VehiclePostingStatus.NotPosted || entry is null || entry.Status != VehicleCustodyStatus.Pending)
            {
                return FleetProblems.PostedAlready();
            }

            tracked.Review(VehicleExpenseStatus.Approved, caller.UserId, now, note);
            entry.Status = VehicleCustodyStatus.Posted;
            entry.UpdatedAt = now;
            entry.UpdatedBy = caller.UserId;
            tracked.PostingStatus = VehiclePostingStatus.Posted;
            tracked.CustodyEntryId = entry.Id;
            tracked.PostedAt = now;

            // The approved reading becomes the vehicle's trusted odometer when it is ahead.
            var vehicle = await db.Vehicles.SingleAsync(candidate => candidate.Id == tracked.VehicleId, cancellationToken);
            if (tracked.OdometerKm > vehicle.OdometerKm)
            {
                vehicle.OdometerKm = tracked.OdometerKm;
                vehicle.OdometerUpdatedAt = now;
                vehicle.OdometerUpdatedBy = caller.UserId;
            }
        }
        else
        {
            tracked.Review(outcome, caller.UserId, now, note);
            if (outcome == VehicleExpenseStatus.Rejected)
            {
                var entry = await db.VehicleCustodyEntries.SingleOrDefaultAsync(candidate => candidate.SourceType == VehicleCustodySources.Expense && candidate.SourceId == tracked.Id, cancellationToken);
                if (entry is { Status: VehicleCustodyStatus.Pending })
                {
                    entry.Status = VehicleCustodyStatus.Released;
                    entry.UpdatedAt = now;
                    entry.UpdatedBy = caller.UserId;
                }
            }
        }

        db.Entry(tracked).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ClaimReviewed(caller.UserId, tracked.Id, outcome.ToString());
        return TypedResults.Ok((await views.LoadAsync(tracked.Id, caller, me?.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> ReverseAsync(
        int claimId, ReasonRequest body, HttpContext httpContext, FleetDbContext db, VehicleExpenseViews views, IEmployeeDirectory employees, CompanyClock clock, FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(body.Reason, VehicleExpenseConfiguration.NoteMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var claim = await db.VehicleExpenses.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == claimId, cancellationToken);
        if (claim is null)
        {
            return FleetProblems.ClaimNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Equipment(claim.VehicleId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        var tracked = await db.VehicleExpenses.Include(candidate => candidate.Flags).SingleAsync(candidate => candidate.Id == claimId, cancellationToken);
        switch (VehicleExpenseRules.Reverse(tracked, caller.UserId))
        {
            case ClaimDecision.OwnRecord:
                return FleetProblems.ReviewerIsRecorder();
            case ClaimDecision.WrongStatus:
                return FleetProblems.WrongStatus("Only an approved, posted claim can be reversed.");
        }

        var now = clock.UtcNow;
        var entry = await db.VehicleCustodyEntries.SingleAsync(candidate => candidate.Id == tracked.CustodyEntryId, cancellationToken);
        entry.Status = VehicleCustodyStatus.Released;
        entry.UpdatedAt = now;
        entry.UpdatedBy = caller.UserId;
        entry.Notes = reason;
        tracked.PostingStatus = VehiclePostingStatus.Reversed;
        tracked.ReversedAt = now;
        tracked.ReversedBy = caller.UserId;
        tracked.ReversalReason = reason;
        tracked.UpdatedAt = now;
        tracked.UpdatedBy = caller.UserId;
        db.Entry(tracked).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ClaimReviewed(caller.UserId, tracked.Id, "Reversed");
        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        return TypedResults.Ok((await views.LoadAsync(tracked.Id, caller, me?.Id, cancellationToken))!);
    }

    /// <summary>A rejected claim goes to HR for investigation, once, in one transaction with the referral. Nothing is deducted here.</summary>
    internal static async Task<Results<Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> ReferAsync(
        int claimId, ReferRequest body, HttpContext httpContext, FleetDbContext db, VehicleExpenseViews views, IEmployeeDirectory employees, IHrReferrals referrals,
        CompanyClock clock, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var subject = Input.OptionalText(body.Subject, 200, errors, "subject", "The subject");
        var details = Input.RequiredText(body.Details, 2000, errors, "details", "The details");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var claim = await db.VehicleExpenses.Include(candidate => candidate.Vehicle).Include(candidate => candidate.Flags).SingleOrDefaultAsync(candidate => candidate.Id == claimId, cancellationToken);
        if (claim is null)
        {
            return FleetProblems.ClaimNotFound();
        }

        if (claim.Status != VehicleExpenseStatus.Rejected)
        {
            return FleetProblems.WrongStatus("Only a rejected claim can be referred to HR.");
        }

        if (claim.HrReferralId is not null)
        {
            return FleetProblems.ReferredAlready();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Entry(claim).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        claim.UpdatedAt = clock.UtcNow;
        claim.UpdatedBy = caller.UserId;
        try
        {
            claim.HrReferralId = await referrals.ReferAsync(
                new HrReferralDraft(HrReferralSources.VehicleExpense, claim.Id, claim.DriverEmployeeId, claim.VehicleId,
                    subject ?? $"Rejected vehicle expense {claim.Number} ({claim.Vehicle.Plate})", details!, claim.Amount, claim.Number, caller.UserId),
                SharedTransaction.Of(db), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsAnyUnique(exception))
        {
            return FleetProblems.ReferredAlready();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ClaimReferred(caller.UserId, claim.Id, claim.HrReferralId!.Value);
        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        return TypedResults.Ok((await views.LoadAsync(claim.Id, caller, me?.Id, cancellationToken))!);
    }
}
