using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>
/// <c>POST /api/projects/{pid}/labor/sheets</c>: an engineer records the day's workers on a project: who, how long,
/// in which trade, the allowances and deductions, and which project items each day went to. Saved as
/// <c>Draft</c>, or <c>Submitted</c> for review with <c>submit: true</c>. Never a cost before someone else approves it.
/// </summary>
/// <remarks>
/// The client sends no rate and no amount: the server computes them (a preview now, frozen at approval).
/// One sheet per engineer, project, day and shift (409 <c>sheet_exists</c>), and a worker once per day and shift
/// on any sheet (409 <c>worker_already_recorded</c>); unique indexes are the last guard for both.
/// </remarks>
internal static class CreateSheet
{
    internal sealed record Request(
        string? WorkDate,
        string? Shift,
        string? Notes,
        bool? Submit,
        IReadOnlyList<LaborLineInput>? Lines) : ISheetFormRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/labor/sheets", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Create)
            .WithName("CreateLaborSheet")
            .WithSummary("Record a day's labor sheet on a project (Draft, or Submitted with submit: true); the server computes the pay");

    internal static async Task<Results<Created<LaborSheetView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
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
        var form = SheetForm.Validate(request, clock.Today, checkDateWindow: true, errors);
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

        if (await SheetExistsAsync(db, projectId, form!, caller.UserId, exceptSheetId: null, cancellationToken))
        {
            return WorkforceProblems.SheetAlreadyExists();
        }

        var settings = options.Value.PaySettings;
        var check = await SheetReferences.ResolveAsync(db, projectItems, projectId, form!, currentSheetId: null, settings, cancellationToken);
        if (check.Invalid is { } invalid)
        {
            return invalid;
        }

        if (check.Conflict is { } conflict)
        {
            return conflict;
        }

        var now = clock.UtcNow;
        var sheet = new DailySheet
        {
            ProjectId = projectId,
            WorkDate = form!.WorkDate,
            Shift = form.Shift,
            RecordedBy = caller.UserId,
            Status = form.Submit ? SheetStatus.Submitted : SheetStatus.Draft,
            SubmittedAt = form.Submit ? now : null,
            Notes = form.Notes,
            CreatedAt = now,
        };
        SheetReferences.AddLines(sheet, check.Lines!, now, caller.UserId);
        db.DailySheets.Add(sheet);

        if (await SheetSaving.SaveAsync(db, cancellationToken) is { } lost)
        {
            return lost;
        }

        log.SheetRecorded(caller.UserId, projectId, sheet.Id, sheet.Lines.Count, sheet.Status.ToString());

        var view = await LaborSheetView.LoadAsync(db, projectItems, users, caller, settings, projectId, sheet.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/labor/sheets/{sheet.Id}", view!);
    }

    /// <summary>The caller already has a live sheet for this project, day and shift (decision W1).</summary>
    internal static Task<bool> SheetExistsAsync(
        WorkforceDbContext db, int projectId, SheetFields form, int recorderUserId, int? exceptSheetId, CancellationToken cancellationToken) =>
        db.DailySheets.AnyAsync(
            sheet => sheet.ProjectId == projectId && sheet.WorkDate == form.WorkDate && sheet.Shift == form.Shift
                && sheet.RecordedBy == recorderUserId && sheet.Status != SheetStatus.Rejected && sheet.Id != (exceptSheetId ?? 0),
            cancellationToken);
}

/// <summary>Turns a lost race (one of the sheet's unique indexes, or a project item deleted meanwhile) into the matching 409.</summary>
internal static class SheetSaving
{
    public static async Task<ProblemHttpResult?> SaveAsync(WorkforceDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, DailySheetConfiguration.RecorderDayIndex))
        {
            return WorkforceProblems.SheetAlreadyExists();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, AttendanceLineConfiguration.WorkerShiftIndex))
        {
            return WorkforceProblems.AlreadyRecorded(null);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsReferenceOrCheck(exception))
        {
            // A project item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return WorkforceProblems.ItemRemoved();
        }
    }
}
