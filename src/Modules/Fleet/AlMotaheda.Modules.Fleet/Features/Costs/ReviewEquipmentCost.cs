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

/// <summary>The body of approve, return and reject. The note is required for return and reject.</summary>
internal sealed record EquipmentCostReviewRequest(string? Note, string? RowVersion);

/// <summary><c>POST /api/projects/{pid}/equipment-costs/{id}/approve</c>: the cost is recognized and frozen.</summary>
internal static class ApproveEquipmentCost
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment-costs/{costId:int}/approve", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.Approve)
            .WithName("ApproveEquipmentCost")
            .WithSummary("Approve a recorded equipment cost (not your own): it becomes a cost and is frozen; custody counts as paid");

    internal static Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int costId, EquipmentCostReviewRequest request, HttpContext httpContext, FleetDbContext db, IProjectAccess access,
        IUserDirectory users, ISupplierDirectory suppliers, CompanyClock clock, FleetLog log, CancellationToken cancellationToken) =>
        EquipmentCostReview.HandleAsync(EquipmentCostStatus.Approved, projectId, costId, request, httpContext, db, access, users, suppliers, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/equipment-costs/{id}/return</c>: back to the recorder to fix, with a note.</summary>
internal static class ReturnEquipmentCost
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment-costs/{costId:int}/return", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.Approve)
            .WithName("ReturnEquipmentCost")
            .WithSummary("Return a recorded equipment cost to its recorder to fix and resubmit (a note is required)");

    internal static Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int costId, EquipmentCostReviewRequest request, HttpContext httpContext, FleetDbContext db, IProjectAccess access,
        IUserDirectory users, ISupplierDirectory suppliers, CompanyClock clock, FleetLog log, CancellationToken cancellationToken) =>
        EquipmentCostReview.HandleAsync(EquipmentCostStatus.Returned, projectId, costId, request, httpContext, db, access, users, suppliers, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/equipment-costs/{id}/reject</c>: final; the cost is never recognized.</summary>
internal static class RejectEquipmentCost
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment-costs/{costId:int}/reject", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.Approve)
            .WithName("RejectEquipmentCost")
            .WithSummary("Reject a recorded equipment cost for good (a note is required)");

    internal static Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int costId, EquipmentCostReviewRequest request, HttpContext httpContext, FleetDbContext db, IProjectAccess access,
        IUserDirectory users, ISupplierDirectory suppliers, CompanyClock clock, FleetLog log, CancellationToken cancellationToken) =>
        EquipmentCostReview.HandleAsync(EquipmentCostStatus.Rejected, projectId, costId, request, httpContext, db, access, users, suppliers, clock, log, cancellationToken);
}

/// <summary>
/// The one review flow behind approve, return and reject: equipment Approve, not the recorder, only from
/// <c>Recorded</c>. It runs under the project lock, so two reviewers can't both decide the same cost and a resubmit
/// can't land in between.
/// </summary>
internal static class EquipmentCostReview
{
    public static async Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        EquipmentCostStatus target,
        int projectId,
        int costId,
        EquipmentCostReviewRequest request,
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
        var note = target == EquipmentCostStatus.Approved
            ? Input.OptionalText(request.Note, EquipmentCostConfiguration.ReviewNoteMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, EquipmentCostConfiguration.ReviewNoteMaxLength, errors, "note", "The note");
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

        switch (EquipmentCostRules.Review(cost.Status, cost.CreatedBy, caller.UserId))
        {
            case CostDecision.OwnRecord:
                log.ActionRefused(caller.UserId, $"Review{target}EquipmentCost", "reviewing own record");
                return FleetProblems.ReviewerIsRecorder();
            case CostDecision.WrongStatus:
                return FleetProblems.WrongStatus($"Only a recorded cost can be reviewed. This one is {cost.Status}.");
        }

        cost.Status = target;
        cost.ReviewedAt = clock.UtcNow;
        cost.ReviewedBy = caller.UserId;
        cost.ReviewNote = note;
        cost.UpdatedAt = cost.ReviewedAt;
        cost.UpdatedBy = caller.UserId;
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
        log.CostReviewed(caller.UserId, projectId, cost.Id, target.ToString());

        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, cost.Id, cancellationToken);
        return view is null ? FleetProblems.CostNotFound() : TypedResults.Ok(view);
    }
}
