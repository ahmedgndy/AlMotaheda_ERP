using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using AlMotaheda.Modules.Subcontractors.Features.Payments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Work;

/// <summary>The body of approve and reject. The note is required to reject.</summary>
internal sealed record WorkReviewRequest(string? Note, string? RowVersion);

/// <summary>The review input, after validation.</summary>
internal sealed record WorkReviewInput(string? Note, byte[] RowVersion)
{
    public static WorkReviewInput? Validate(WorkStatus target, WorkReviewRequest request, ValidationErrors errors)
    {
        var note = target == WorkStatus.Approved
            ? Input.OptionalText(request.Note, WorkReview.ReviewNoteMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, WorkReview.ReviewNoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);

        return errors.HasErrors ? null : new WorkReviewInput(note, rowVersion!);
    }
}

/// <summary>
/// <c>POST /api/projects/{pid}/subcontract-measurements/{id}/approve</c>: the measured work becomes cost. Unapplied
/// advances of the contract are applied to it at once, in the same transaction.
/// </summary>
internal static class ApproveMeasurement
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontract-measurements/{measurementId:int}/approve", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Approve)
            .WithName("ApproveMeasurement")
            .WithSummary("Approve a recorded measurement (not your own): it becomes cost, and unapplied advances settle it");

    internal static Task<Results<Ok<MeasurementView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int measurementId, WorkReviewRequest request, HttpContext httpContext, SubcontractorsDbContext db,
        IProjectAccess access, ContractDetails details, CompanyClock clock, SubcontractorsLog log, CancellationToken cancellationToken) =>
        MeasurementReview.HandleAsync(WorkStatus.Approved, projectId, measurementId, request, httpContext, db, access, details, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/subcontract-measurements/{id}/reject</c>: final; the work is never cost.</summary>
internal static class RejectMeasurement
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontract-measurements/{measurementId:int}/reject", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Approve)
            .WithName("RejectMeasurement")
            .WithSummary("Reject a recorded measurement for good (a note is required)");

    internal static Task<Results<Ok<MeasurementView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int measurementId, WorkReviewRequest request, HttpContext httpContext, SubcontractorsDbContext db,
        IProjectAccess access, ContractDetails details, CompanyClock clock, SubcontractorsLog log, CancellationToken cancellationToken) =>
        MeasurementReview.HandleAsync(WorkStatus.Rejected, projectId, measurementId, request, httpContext, db, access, details, clock, log, cancellationToken);
}

/// <summary>
/// The one review flow behind approve and reject: subcontractors Approve, not the recorder, only from
/// <c>Recorded</c>. Approval checks the item quantity again and settles unapplied payments, all under the project
/// lock, so two reviewers can't both pass the check.
/// </summary>
internal static class MeasurementReview
{
    public static async Task<Results<Ok<MeasurementView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        WorkStatus target,
        int projectId,
        int measurementId,
        WorkReviewRequest request,
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
        var input = WorkReviewInput.Validate(target, request, errors);
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

        var measurement = await db.ProgressMeasurements
            .Include(candidate => candidate.ContractItem)
            .SingleOrDefaultAsync(candidate => candidate.Id == measurementId && candidate.ContractItem.Contract.ProjectId == projectId, cancellationToken);
        if (measurement is null)
        {
            return SubcontractorsProblems.MeasurementNotFound();
        }

        switch (WorkRules.Review(measurement.Status, measurement.CreatedBy, caller.UserId))
        {
            case ReviewDecision.OwnRecord:
                log.ActionRefused(caller.UserId, $"Review{target}", "reviewing own measurement");
                return SubcontractorsProblems.ReviewerIsRecorder();
            case ReviewDecision.WrongStatus:
                return SubcontractorsProblems.WrongStatus($"Only a recorded measurement can be reviewed. This one is {measurement.Status}.");
        }

        if (target == WorkStatus.Approved)
        {
            // The item quantity may have been measured again since this was recorded: check once more.
            var otherMeasured = await db.ProgressMeasurements
                .Where(other => other.ContractItemId == measurement.ContractItemId && other.Id != measurement.Id && other.Status != WorkStatus.Rejected)
                .SumAsync(other => other.Quantity, cancellationToken);

            if (!WorkRules.FitsItemQuantity(measurement.ContractItem.Quantity, otherMeasured, measurement.Quantity))
            {
                return SubcontractorsProblems.OverQuantity(WorkRules.RemainingQuantity(measurement.ContractItem.Quantity, otherMeasured));
            }
        }

        var now = clock.UtcNow;
        measurement.Status = target;
        measurement.ReviewedAt = now;
        measurement.ReviewedBy = caller.UserId;
        measurement.ReviewNote = input!.Note;
        db.Entry(measurement).Property(candidate => candidate.RowVersion).OriginalValue = input.RowVersion;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        var allocations = 0;
        if (target == WorkStatus.Approved)
        {
            allocations = await ContractSettlement.ApplyAsync(db, measurement.ContractItem.ContractId, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        log.MeasurementReviewed(caller.UserId, projectId, measurement.Id, target.ToString(), allocations);

        var view = await details.MeasurementAsync(projectId, measurement.Id, caller.SeesMoney, cancellationToken);
        return view is null ? SubcontractorsProblems.MeasurementNotFound() : TypedResults.Ok(view);
    }
}
