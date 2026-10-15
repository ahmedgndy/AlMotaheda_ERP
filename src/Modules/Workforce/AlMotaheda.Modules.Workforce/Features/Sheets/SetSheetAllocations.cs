using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>
/// <c>PUT /api/projects/{pid}/labor/sheets/{id}/allocations</c>: set which project items the days of some lines
/// went to. Lines not listed keep their allocations; a listed line's allocations are replaced (an empty list
/// unlinks it). This is how "not linked" labor gets linked after the fact.
/// </summary>
/// <remarks>
/// Allowed for the recorder while the sheet is Draft or Returned, and for anyone with workers Edit or workers Approve
/// while it isn't rejected. The endpoint takes the signed-in policy and checks these itself, because
/// <c>RequirePermission</c> asks for exactly one action. Runs under the project lock with the sheet's row version.
/// </remarks>
internal static class SetSheetAllocations
{
    internal sealed record LineRequest(int? LineId, IReadOnlyList<LaborPortionInput>? Allocations);

    internal sealed record Request(string? RowVersion, IReadOnlyList<LineRequest>? Lines);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/projects/{projectId:int}/labor/sheets/{sheetId:int}/allocations", HandleAsync)
            .WithName("SetLaborSheetAllocations")
            .WithSummary("Set the project items of some lines of a labor sheet (the recorder while Draft or Returned, or workers Edit / Approve)");

    internal static async Task<Results<Ok<LaborSheetView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int sheetId,
        Request request,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IProjectItems projectItems,
        IUserDirectory users,
        IOptions<WorkforceOptions> options,
        CompanyClock clock,
        WorkforceLog log,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var canEditOrApprove = caller.Can(PermissionAction.Edit) || caller.Can(PermissionAction.Approve);
        var canCreate = caller.Can(PermissionAction.Create);
        if (!canEditOrApprove && !canCreate)
        {
            return WorkforceProblems.AllocationNotAllowed();
        }

        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        var changes = Validate(request.Lines, errors);
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
            .Include(candidate => candidate.Lines).ThenInclude(line => line.Allocations)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == sheetId && candidate.ProjectId == projectId, cancellationToken);
        if (sheet is null)
        {
            return WorkforceProblems.SheetNotFound();
        }

        switch (SheetRules.Allocate(sheet.Status, sheet.RecordedBy, caller.UserId, canEditOrApprove, canCreate))
        {
            case SheetDecision.NotRecorder:
                log.ActionRefused(caller.UserId, "SetLaborSheetAllocations", "not allowed on this sheet");
                return WorkforceProblems.AllocationNotAllowed();
            case SheetDecision.WrongStatus:
                return WorkforceProblems.WrongStatus("A rejected sheet's lines are void; their allocations can't change.");
        }

        var outcome = await AllocationChanges.ApplyAsync(db, projectItems, projectId, sheet, changes!, caller.UserId, clock.UtcNow, cancellationToken);
        if (outcome.Invalid is { } invalid)
        {
            return invalid;
        }

        if (outcome.Conflict is { } conflict)
        {
            return conflict;
        }

        db.Entry(sheet).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsReferenceOrCheck(exception))
        {
            // A project item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return WorkforceProblems.ItemRemoved();
        }

        await transaction.CommitAsync(cancellationToken);
        log.AllocationsChanged(caller.UserId, projectId, sheet.Id, changes!.Count);

        var view = await LaborSheetView.LoadAsync(db, projectItems, users, caller, options.Value.PaySettings, projectId, sheet.Id, cancellationToken);
        return view is null ? WorkforceProblems.SheetNotFound() : TypedResults.Ok(view);
    }

    private static List<LineAllocationChange>? Validate(IReadOnlyList<LineRequest>? lines, ValidationErrors errors)
    {
        if (lines is null || lines.Count == 0)
        {
            errors.Add("lines", "At least one line is required.");
            return null;
        }

        if (lines.Count > SheetForm.MaxLines)
        {
            errors.Add("lines", $"At most {SheetForm.MaxLines} lines at once.");
            return null;
        }

        var changes = new List<LineAllocationChange>(lines.Count);
        var seen = new HashSet<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            var field = $"lines[{index}]";
            var line = lines[index];
            if (line?.LineId is not { } lineId || lineId < 1)
            {
                errors.Add($"{field}.lineId", "lineId is required.");
                continue;
            }

            if (!seen.Add(lineId))
            {
                errors.Add($"{field}.lineId", "The same line is listed twice.");
                continue;
            }

            if (SheetForm.ValidatePortions(line.Allocations, $"{field}.allocations", errors) is { } allocations)
            {
                changes.Add(new LineAllocationChange(lineId, allocations, $"{field}.allocations"));
            }
        }

        return errors.HasErrors ? null : changes;
    }
}
