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
/// <c>POST /api/projects/{pid}/labor/sheets/{id}/submit</c>: the recorder sends a Draft or Returned sheet for review.
/// The workers are checked again (still active, still assigned that day): either may have changed since the draft.
/// </summary>
internal static class SubmitSheet
{
    internal sealed record Request(string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/labor/sheets/{sheetId:int}/submit", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Create)
            .WithName("SubmitLaborSheet")
            .WithSummary("Send a Draft or Returned labor sheet for review (its recorder only)");

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

        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
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

        var sheet = await db.DailySheets
            .SingleOrDefaultAsync(candidate => candidate.Id == sheetId && candidate.ProjectId == projectId, cancellationToken);
        if (sheet is null)
        {
            return WorkforceProblems.SheetNotFound();
        }

        switch (SheetRules.Edit(sheet.Status, sheet.RecordedBy, caller.UserId))
        {
            case SheetDecision.NotRecorder:
                log.ActionRefused(caller.UserId, "SubmitLaborSheet", "not the recorder");
                return WorkforceProblems.NotTheRecorder();
            case SheetDecision.WrongStatus:
                return WorkforceProblems.WrongStatus($"Only a Draft or Returned sheet can be submitted. This one is {sheet.Status}.");
        }

        var workers = await db.AttendanceLines.AsNoTracking()
            .Where(line => line.SheetId == sheet.Id)
            .Select(line => new WorkerFacts(line.Worker.Id, line.Worker.Status, line.Worker.TradeId, line.Worker.DailyRate))
            .ToListAsync(cancellationToken);

        if (await SheetReferences.CheckWorkersAsync(db, projectId, sheet.WorkDate, workers.ToDictionary(worker => worker.Id), cancellationToken) is { } refused)
        {
            return refused.Problem;
        }

        var oldStatus = sheet.Status;
        var now = clock.UtcNow;
        sheet.Submit(caller.UserId, now);

        db.Entry(sheet).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.SheetSubmitted(caller.UserId, projectId, sheet.Id, oldStatus.ToString());

        var view = await LaborSheetView.LoadAsync(db, projectItems, users, caller, options.Value.PaySettings, projectId, sheet.Id, cancellationToken);
        return view is null ? WorkforceProblems.SheetNotFound() : TypedResults.Ok(view);
    }
}
