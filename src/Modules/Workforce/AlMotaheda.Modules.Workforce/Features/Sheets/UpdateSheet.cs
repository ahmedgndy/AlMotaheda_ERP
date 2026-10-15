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
/// <c>PUT /api/projects/{pid}/labor/sheets/{id}</c>: the recorder fixes a Draft or Returned sheet. The lines are
/// replaced as a whole; with <c>submit: true</c> the sheet goes (back) to review.
/// </summary>
/// <remarks>
/// The old lines are deleted and the new ones inserted in one transaction, in two steps, so a worker kept on the
/// sheet never collides with their own old line on the "once per day and shift" index. Nothing on these lines is
/// paid or approved yet, so deleting them loses no history.
/// </remarks>
internal static class UpdateSheet
{
    internal sealed record Request(
        string? WorkDate,
        string? Shift,
        string? Notes,
        bool? Submit,
        IReadOnlyList<LaborLineInput>? Lines,
        string? RowVersion) : ISheetFormRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/projects/{projectId:int}/labor/sheets/{sheetId:int}", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Create)
            .WithName("UpdateLaborSheet")
            .WithSummary("Fix a Draft or Returned labor sheet (its recorder only); replaces the lines; submit: true sends it for review");

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
        var form = SheetForm.Validate(request, clock.Today, checkDateWindow: false, errors);
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
            .Include(candidate => candidate.Lines).ThenInclude(line => line.Adjustments)
            .Include(candidate => candidate.Lines).ThenInclude(line => line.Allocations)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == sheetId && candidate.ProjectId == projectId, cancellationToken);
        if (sheet is null)
        {
            return WorkforceProblems.SheetNotFound();
        }

        switch (SheetRules.Edit(sheet.Status, sheet.RecordedBy, caller.UserId))
        {
            case SheetDecision.NotRecorder:
                log.ActionRefused(caller.UserId, "UpdateLaborSheet", "not the recorder");
                return WorkforceProblems.NotTheRecorder();
            case SheetDecision.WrongStatus:
                return WorkforceProblems.WrongStatus($"Only a Draft or Returned sheet can be edited. This one is {sheet.Status}.");
        }

        if (form!.WorkDate != sheet.WorkDate && !SheetRules.WorkDateAllowed(form.WorkDate, clock.Today))
        {
            errors.Add("workDate", SheetForm.WorkDateWindowMessage);
            return WorkforceProblems.Validation(errors);
        }

        if (await CreateSheet.SheetExistsAsync(db, projectId, form, caller.UserId, exceptSheetId: sheet.Id, cancellationToken))
        {
            return WorkforceProblems.SheetAlreadyExists();
        }

        var settings = options.Value.PaySettings;
        var check = await SheetReferences.ResolveAsync(db, projectItems, projectId, form, currentSheetId: sheet.Id, settings, cancellationToken);
        if (check.Invalid is { } invalid)
        {
            return invalid;
        }

        if (check.Conflict is { } conflict)
        {
            return conflict;
        }

        var now = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Step 1: the sheet itself (checked against the caller's row version) and the removal of the old lines.
        // Copies of the lists: removing an entity can make EF take it out of the navigation being read.
        var oldLines = sheet.Lines.ToList();
        foreach (var line in oldLines)
        {
            db.LaborAllocations.RemoveRange(line.Allocations.ToList());
            db.LineAdjustments.RemoveRange(line.Adjustments.ToList());
        }

        db.AttendanceLines.RemoveRange(oldLines);

        var oldStatus = sheet.Status;
        sheet.WorkDate = form.WorkDate;
        sheet.Shift = form.Shift;
        sheet.Notes = form.Notes;
        sheet.UpdatedAt = now;
        sheet.UpdatedBy = caller.UserId;
        if (form.Submit)
        {
            sheet.Status = SheetStatus.Submitted;
            sheet.SubmittedAt = now;
        }

        db.Entry(sheet).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        if (await SheetSaving.SaveAsync(db, cancellationToken) is { } lost)
        {
            return lost;
        }

        // Step 2: the new lines.
        sheet.Lines.Clear();
        SheetReferences.AddLines(sheet, check.Lines!, now, caller.UserId);

        if (await SheetSaving.SaveAsync(db, cancellationToken) is { } lostLines)
        {
            return lostLines;
        }

        await transaction.CommitAsync(cancellationToken);
        log.SheetEdited(caller.UserId, projectId, sheet.Id, sheet.Lines.Count, sheet.Status.ToString());
        if (form.Submit)
        {
            log.SheetSubmitted(caller.UserId, projectId, sheet.Id, oldStatus.ToString());
        }

        var view = await LaborSheetView.LoadAsync(db, projectItems, users, caller, settings, projectId, sheet.Id, cancellationToken);
        return view is null ? WorkforceProblems.SheetNotFound() : TypedResults.Ok(view);
    }
}
