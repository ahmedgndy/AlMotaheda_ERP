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

/// <summary>The body of approve, return and reject. The note is required for return and reject.</summary>
internal sealed record SheetReviewRequest(string? Note, string? RowVersion);

/// <summary>
/// <c>POST /api/projects/{pid}/labor/sheets/{id}/approve</c>: the sheet becomes labor cost. Each line's rate and
/// pay are computed now and copied onto the line, and each item allocation gets its amount; none of it changes again.
/// </summary>
internal static class ApproveSheet
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/labor/sheets/{sheetId:int}/approve", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Approve)
            .WithName("ApproveLaborSheet")
            .WithSummary("Approve a submitted labor sheet (not your own): the pay is frozen and becomes labor cost");

    internal static Task<Results<Ok<LaborSheetView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int sheetId, SheetReviewRequest request, HttpContext httpContext, WorkforceDbContext db, IProjectAccess access,
        IProjectItems projectItems, IUserDirectory users, IOptions<WorkforceOptions> options, CompanyClock clock, WorkforceLog log,
        CancellationToken cancellationToken) =>
        SheetReview.HandleAsync(SheetStatus.Approved, projectId, sheetId, request, httpContext, db, access, projectItems, users, options, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/labor/sheets/{id}/return</c>: back to the recorder to fix and resubmit, with a note.</summary>
internal static class ReturnSheet
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/labor/sheets/{sheetId:int}/return", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Approve)
            .WithName("ReturnLaborSheet")
            .WithSummary("Return a submitted labor sheet to its recorder to fix (a note is required)");

    internal static Task<Results<Ok<LaborSheetView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int sheetId, SheetReviewRequest request, HttpContext httpContext, WorkforceDbContext db, IProjectAccess access,
        IProjectItems projectItems, IUserDirectory users, IOptions<WorkforceOptions> options, CompanyClock clock, WorkforceLog log,
        CancellationToken cancellationToken) =>
        SheetReview.HandleAsync(SheetStatus.Returned, projectId, sheetId, request, httpContext, db, access, projectItems, users, options, clock, log, cancellationToken);
}

/// <summary>
/// <c>POST /api/projects/{pid}/labor/sheets/{id}/reject</c>: final. The lines become void: they count nowhere, and the
/// workers' days are free to be recorded again.
/// </summary>
internal static class RejectSheet
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/labor/sheets/{sheetId:int}/reject", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Approve)
            .WithName("RejectLaborSheet")
            .WithSummary("Reject a submitted labor sheet for good (a note is required); its lines become void");

    internal static Task<Results<Ok<LaborSheetView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int sheetId, SheetReviewRequest request, HttpContext httpContext, WorkforceDbContext db, IProjectAccess access,
        IProjectItems projectItems, IUserDirectory users, IOptions<WorkforceOptions> options, CompanyClock clock, WorkforceLog log,
        CancellationToken cancellationToken) =>
        SheetReview.HandleAsync(SheetStatus.Rejected, projectId, sheetId, request, httpContext, db, access, projectItems, users, options, clock, log, cancellationToken);
}

/// <summary>
/// The one review flow behind approve, return and reject: workers Approve, not the recorder, only from
/// <c>Submitted</c>. It runs under the project lock, so two reviewers can't both decide the same sheet and an
/// allocation change can't land halfway through an approval.
/// </summary>
internal static class SheetReview
{
    public static async Task<Results<Ok<LaborSheetView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        SheetStatus target,
        int projectId,
        int sheetId,
        SheetReviewRequest request,
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
        var note = target == SheetStatus.Approved
            ? Input.OptionalText(request.Note, DailySheetConfiguration.ReviewNoteMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, DailySheetConfiguration.ReviewNoteMaxLength, errors, "note", "The note");
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

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return WorkforceProblems.TryAgainLater();
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

        switch (SheetRules.Review(sheet.Status, sheet.RecordedBy, caller.UserId))
        {
            case SheetDecision.OwnRecord:
                log.ActionRefused(caller.UserId, $"Review{target}", "reviewing own sheet");
                return WorkforceProblems.ReviewerIsRecorder();
            case SheetDecision.WrongStatus:
                return WorkforceProblems.WrongStatus($"Only a submitted sheet can be reviewed. This one is {sheet.Status}.");
        }

        if (target == SheetStatus.Approved)
        {
            var rates = await SheetQueries.RateRows(db, sheet.Id).ToDictionaryAsync(row => row.LineId, cancellationToken);
            var settings = options.Value.PaySettings;

            foreach (var line in sheet.Lines.Where(line => !line.IsVoid))
            {
                var pay = SheetApproval.FreezePay(line, rates[line.Id].Rate, settings);
                if (pay.IsNegative)
                {
                    return WorkforceProblems.NetBelowZero(line.WorkerId);
                }
            }
        }
        else if (target == SheetStatus.Rejected)
        {
            foreach (var line in sheet.Lines)
            {
                line.IsVoid = true;
            }
        }

        var now = clock.UtcNow;
        sheet.Review(target, caller.UserId, now, note);

        db.Entry(sheet).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.SheetReviewed(caller.UserId, projectId, sheet.Id, target.ToString());

        var view = await LaborSheetView.LoadAsync(db, projectItems, users, caller, options.Value.PaySettings, projectId, sheet.Id, cancellationToken);
        return view is null ? WorkforceProblems.SheetNotFound() : TypedResults.Ok(view);
    }
}

/// <summary>Copies the computed pay onto a line at approval, and the item amounts onto its allocations.</summary>
internal static class SheetApproval
{
    /// <summary>
    /// Computes the line's pay with today's rates and settings and copies it onto the line. Returns the pay; when it
    /// is negative nothing should be saved (the caller refuses with 409 <c>negative_net</c>).
    /// </summary>
    public static LinePay FreezePay(AttendanceLine line, RateInputs rate, LaborPaySettings settings)
    {
        var pay = LaborPay.Compute(
            rate,
            line.Days,
            line.OvertimeHours,
            line.Adjustments.Select(adjustment => new PayAdjustment(adjustment.Kind, adjustment.Amount)),
            settings);

        if (pay.IsNegative)
        {
            return pay;
        }

        line.AppliedDailyRate = pay.DailyRate;
        line.BaseAmount = pay.BaseAmount;
        line.OvertimeAmount = pay.OvertimeAmount;
        line.AllowancesAmount = pay.AllowancesAmount;
        line.DeductionsAmount = pay.DeductionsAmount;
        line.NetAmount = pay.NetAmount;

        ApplyAmounts(line);
        return pay;
    }

    /// <summary>
    /// Gives each allocation of an approved line its share of the net (by item id, the last one taking the
    /// rounding rest). Called at approval and whenever an approved line is re-allocated; the net never changes.
    /// </summary>
    public static void ApplyAmounts(AttendanceLine line)
    {
        if (line.NetAmount is not { } net)
        {
            return;
        }

        var ordered = line.Allocations.OrderBy(allocation => allocation.ProjectItemId).ToList();
        var amounts = LaborAllocator.Amounts(net, ordered.Select(allocation => allocation.Portion).ToList());
        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index].Amount = amounts[index];
        }
    }
}
