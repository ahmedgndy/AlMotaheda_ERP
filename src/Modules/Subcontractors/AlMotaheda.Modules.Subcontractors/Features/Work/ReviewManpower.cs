using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.ContractItems;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using AlMotaheda.Modules.Subcontractors.Features.Payments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Work;

/// <summary>
/// <c>POST /api/projects/{pid}/subcontract-manpower/{id}/approve</c>: the sheet becomes cost, and unapplied
/// advances of the contract settle it in the same transaction.
/// </summary>
internal static class ApproveManpower
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontract-manpower/{sheetId:int}/approve", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Approve)
            .WithName("ApproveManpowerSheet")
            .WithSummary("Approve a recorded manpower sheet (not your own): it becomes cost, and unapplied advances settle it");

    internal static Task<Results<Ok<ManpowerView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int sheetId, WorkReviewRequest request, HttpContext httpContext, SubcontractorsDbContext db,
        IProjectAccess access, ContractDetails details, CompanyClock clock, SubcontractorsLog log, CancellationToken cancellationToken) =>
        ManpowerReview.HandleAsync(WorkStatus.Approved, projectId, sheetId, request, httpContext, db, access, details, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/subcontract-manpower/{id}/reject</c>: final; the sheet is never cost.</summary>
internal static class RejectManpower
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontract-manpower/{sheetId:int}/reject", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Approve)
            .WithName("RejectManpowerSheet")
            .WithSummary("Reject a recorded manpower sheet for good (a note is required)");

    internal static Task<Results<Ok<ManpowerView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int sheetId, WorkReviewRequest request, HttpContext httpContext, SubcontractorsDbContext db,
        IProjectAccess access, ContractDetails details, CompanyClock clock, SubcontractorsLog log, CancellationToken cancellationToken) =>
        ManpowerReview.HandleAsync(WorkStatus.Rejected, projectId, sheetId, request, httpContext, db, access, details, clock, log, cancellationToken);
}

/// <summary>
/// The review flow of a manpower sheet: the same rules as a measurement. Approval checks the contract ceiling
/// again (the contract value may have been lowered since) and settles unapplied payments, under the project lock.
/// </summary>
internal static class ManpowerReview
{
    public static async Task<Results<Ok<ManpowerView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        WorkStatus target,
        int projectId,
        int sheetId,
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

        var sheet = await db.ManpowerSheets
            .Include(candidate => candidate.Contract)
            .SingleOrDefaultAsync(candidate => candidate.Id == sheetId && candidate.Contract.ProjectId == projectId, cancellationToken);
        if (sheet is null)
        {
            return SubcontractorsProblems.ManpowerSheetNotFound();
        }

        switch (WorkRules.Review(sheet.Status, sheet.CreatedBy, caller.UserId))
        {
            case ReviewDecision.OwnRecord:
                log.ActionRefused(caller.UserId, $"Review{target}", "reviewing own manpower sheet");
                return SubcontractorsProblems.ReviewerIsRecorder();
            case ReviewDecision.WrongStatus:
                return SubcontractorsProblems.WrongStatus($"Only a recorded manpower sheet can be reviewed. This one is {sheet.Status}.");
        }

        if (target == WorkStatus.Approved)
        {
            var others = await db.ManpowerSheets
                .Where(other => other.ContractId == sheet.ContractId && other.Id != sheet.Id && other.Status != WorkStatus.Rejected)
                .SumAsync(other => other.Amount, cancellationToken);

            if (!WorkRules.FitsCeiling(sheet.Contract.Value, others, sheet.Amount))
            {
                return SubcontractorsProblems.OverCeiling(Math.Max(0, sheet.Contract.Value - others));
            }
        }

        var now = clock.UtcNow;
        sheet.Review(target, caller.UserId, now, input!.Note);
        db.Entry(sheet).Property(candidate => candidate.RowVersion).OriginalValue = input.RowVersion;

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
            allocations = await ContractSettlement.ApplyAsync(db, sheet.ContractId, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        log.ManpowerReviewed(caller.UserId, projectId, sheet.Id, target.ToString(), allocations);

        var view = await details.ManpowerAsync(projectId, sheet.Id, caller.SeesMoney, cancellationToken);
        return view is null ? SubcontractorsProblems.ManpowerSheetNotFound() : TypedResults.Ok(view);
    }
}

/// <summary>
/// <c>PUT /api/projects/{pid}/subcontract-manpower/{id}/project-item</c>: link a manpower sheet to a project item,
/// move it, or remove the link. Only the link changes, never an amount; allowed after approval.
/// </summary>
internal static class LinkManpowerItem
{
    internal sealed record Request(int? ProjectItemId, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/projects/{projectId:int}/subcontract-manpower/{sheetId:int}/project-item", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Edit)
            .WithName("LinkManpowerSheetToProjectItem")
            .WithSummary("Link a manpower sheet to a project item, move it, or remove the link");

    internal static async Task<Results<Ok<ManpowerView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int sheetId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        IProjectItems projectItems,
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
        if (request.ProjectItemId is < 1)
        {
            errors.Add("projectItemId", "projectItemId is not valid.");
        }

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

        var sheet = await db.ManpowerSheets
            .SingleOrDefaultAsync(candidate => candidate.Id == sheetId && candidate.Contract.ProjectId == projectId, cancellationToken);
        if (sheet is null)
        {
            return SubcontractorsProblems.ManpowerSheetNotFound();
        }

        if (!WorkRules.CanLink(sheet.Status))
        {
            return SubcontractorsProblems.WrongStatus("A rejected sheet is never cost, so it can't be linked to an item.");
        }

        if (request.ProjectItemId is { } projectItemId && projectItemId != sheet.ProjectItemId)
        {
            switch (await ProjectItemLinks.CheckAsync(projectItems, projectId, projectItemId, cancellationToken))
            {
                case ProjectItemCheck.NotFound:
                    errors.Add("projectItemId", "The project item was not found on this project.");
                    return SubcontractorsProblems.Validation(errors);
                case ProjectItemCheck.Inactive:
                    return SubcontractorsProblems.InactiveItem();
            }
        }

        var previousItemId = sheet.ProjectItemId;
        sheet.ProjectItemId = request.ProjectItemId;
        sheet.UpdatedAt = clock.UtcNow;
        sheet.UpdatedBy = caller.UserId;
        db.Entry(sheet).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (request.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The project item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return SubcontractorsProblems.ItemRemoved();
        }

        log.ManpowerLinked(caller.UserId, projectId, sheet.Id, sheet.ProjectItemId, previousItemId);

        var view = await details.ManpowerAsync(projectId, sheet.Id, caller.SeesMoney, cancellationToken);
        return view is null ? SubcontractorsProblems.ManpowerSheetNotFound() : TypedResults.Ok(view);
    }
}
