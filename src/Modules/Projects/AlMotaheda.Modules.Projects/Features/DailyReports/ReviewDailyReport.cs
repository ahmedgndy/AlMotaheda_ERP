using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.DailyReports;

/// <summary>The body of review and return. The note is required to return.</summary>
internal sealed record DailyReportReviewRequest(string? Note, string? RowVersion);

/// <summary>
/// <c>POST /api/projects/{pid}/daily-reports/{id}/review</c>: the report is accepted and final. Reviewing creates
/// no subcontract measurement and no labor cost (decision D2).
/// </summary>
internal static class ReviewDailyReport
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/daily-reports/{reportId:int}/review", HandleAsync)
            .RequirePermission(SitePermissions.Module, PermissionAction.Approve)
            .WithName("ReviewDailyReport")
            .WithSummary("Mark a submitted daily report as reviewed (an Executive or the project's manager, not the author)");

    internal static Task<Results<Ok<DailyReportView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int reportId, DailyReportReviewRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        DailyReportReview.HandleAsync(DailyReportStatus.Reviewed, projectId, reportId, request, httpContext, db, access, users, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/daily-reports/{id}/return</c>: back to the author to fix, with a note.</summary>
internal static class ReturnDailyReport
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/daily-reports/{reportId:int}/return", HandleAsync)
            .RequirePermission(SitePermissions.Module, PermissionAction.Approve)
            .WithName("ReturnDailyReport")
            .WithSummary("Return a submitted daily report to its author for correction (a note is required)");

    internal static Task<Results<Ok<DailyReportView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int reportId, DailyReportReviewRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        DailyReportReview.HandleAsync(DailyReportStatus.Returned, projectId, reportId, request, httpContext, db, access, users, clock, log, cancellationToken);
}

/// <summary>
/// The one flow behind review and return: site Approve, an Executive or the project's manager, never the author,
/// only from <c>Submitted</c>. The row version stops a review from landing on a report the author just changed.
/// </summary>
internal static class DailyReportReview
{
    public static async Task<Results<Ok<DailyReportView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        DailyReportStatus target,
        int projectId,
        int reportId,
        DailyReportReviewRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = target == DailyReportStatus.Reviewed
            ? Input.OptionalText(request.Note, DailyReportConfiguration.ReviewNoteMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, DailyReportConfiguration.ReviewNoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (!DailyReportRules.CanReview(project))
        {
            log.ActionRefused(caller.UserId, $"DailyReport{target}", "not an Executive or the project's manager");
            return PlanningProblems.ReportsReviewedByManager();
        }

        if (project.WritesBlocked)
        {
            return ProjectProblems.CompletedProject();
        }

        var report = await db.DailyReports
            .SingleOrDefaultAsync(candidate => candidate.Id == reportId && candidate.ProjectId == projectId, cancellationToken);
        if (report is null)
        {
            return PlanningProblems.DailyReportNotFound();
        }

        switch (DailyReportRules.Review(report.Status, report.AuthorUserId, caller.UserId))
        {
            case ReportDecision.OwnReport:
                log.ActionRefused(caller.UserId, $"DailyReport{target}", "reviewing own report");
                return WorkspaceProblems.ReviewerIsRecorder();
            case ReportDecision.WrongStatus:
                return WorkspaceProblems.WrongStatus($"Only a submitted report can be reviewed or returned. This one is {report.Status}.");
        }

        report.Review(target, caller.UserId, clock.UtcNow, note);
        db.Entry(report).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.DailyReportReviewed(caller.UserId, projectId, report.Id, target.ToString());

        var view = await DailyReportView.LoadAsync(db, users, projectId, report.Id, caller.UserId, callerCanReview: true, cancellationToken);
        return view is null ? PlanningProblems.DailyReportNotFound() : TypedResults.Ok(view);
    }
}
