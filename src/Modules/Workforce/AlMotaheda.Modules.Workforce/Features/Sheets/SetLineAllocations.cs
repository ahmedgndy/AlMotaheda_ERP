using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>
/// <c>PUT /api/projects/{pid}/labor/lines/{lineId}/allocations</c>: set the project items of one worker's day, for
/// example from the "not linked" list of the project items screen. Needs workers Edit or Approve.
/// </summary>
/// <remarks>
/// It takes no row version (the design's body is only the allocations): the project lock makes it one change at a
/// time, and it moves the sheet's row version, so a reviewer holding an older copy of the sheet must reload it.
/// </remarks>
internal static class SetLineAllocations
{
    internal sealed record Request(IReadOnlyList<LaborPortionInput>? Allocations);

    /// <summary>The line after the change, with its allocations.</summary>
    internal sealed record Response(int LineId, int SheetId, string SheetStatus, IReadOnlyList<LaborAllocationView> Allocations, decimal UnallocatedPortion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/projects/{projectId:int}/labor/lines/{lineId:int}/allocations", HandleAsync)
            .WithName("SetLaborLineAllocations")
            .WithSummary("Set the project items of one labor line (workers Edit or Approve); an empty list unlinks it");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int lineId,
        Request request,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IProjectItems projectItems,
        CompanyClock clock,
        WorkforceLog log,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (!caller.Can(PermissionAction.Edit) && !caller.Can(PermissionAction.Approve))
        {
            return WorkforceProblems.AllocationNotAllowed();
        }

        var errors = new ValidationErrors();
        if (request.Allocations is null)
        {
            errors.Add("allocations", "allocations is required (an empty list removes every link).");
        }

        var allocations = SheetForm.ValidatePortions(request.Allocations, "allocations", errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return WorkforceProblems.CompletedProject();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return WorkforceProblems.TryAgainLater();
        }

        var sheet = await db.DailySheets
            .Include(candidate => candidate.Lines.Where(line => line.Id == lineId)).ThenInclude(line => line.Allocations)
            .SingleOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Lines.Any(line => line.Id == lineId), cancellationToken);
        if (sheet is null || sheet.Lines.Single().IsVoid)
        {
            return WorkforceProblems.LineNotFound();
        }

        if (sheet.Status == SheetStatus.Rejected)
        {
            return WorkforceProblems.WrongStatus("A rejected sheet's lines are void; their allocations can't change.");
        }

        var outcome = await AllocationChanges.ApplyAsync(
            db, projectItems, projectId, sheet, [new LineAllocationChange(lineId, allocations!, "allocations")], caller.UserId, clock.UtcNow, cancellationToken);
        if (outcome.Invalid is { } invalid)
        {
            return invalid;
        }

        if (outcome.Conflict is { } conflict)
        {
            return conflict;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsReferenceOrCheck(exception))
        {
            // A project item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return WorkforceProblems.ItemRemoved();
        }

        await transaction.CommitAsync(cancellationToken);
        log.AllocationsChanged(caller.UserId, projectId, sheet.Id, 1);

        var line = sheet.Lines.Single();
        var itemIds = line.Allocations.Select(allocation => allocation.ProjectItemId).ToArray();
        var items = await projectItems.FindAsync(projectId, itemIds, cancellationToken);
        var seesMoney = caller.SeesMoney;

        return TypedResults.Ok(new Response(
            line.Id,
            sheet.Id,
            sheet.Status.ToString(),
            line.Allocations
                .OrderBy(allocation => allocation.ProjectItemId)
                .Select(allocation => new LaborAllocationView(
                    allocation.Id,
                    allocation.ProjectItemId,
                    items.GetValueOrDefault(allocation.ProjectItemId)?.NameAr,
                    items.GetValueOrDefault(allocation.ProjectItemId)?.NameEn,
                    allocation.Portion,
                    seesMoney ? allocation.Amount : null))
                .ToList(),
            LaborAllocator.UnallocatedPortion(line.Allocations.Select(allocation => allocation.Portion))));
    }
}
