using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.DailyReports;

/// <summary>
/// <c>PUT /api/projects/{pid}/daily-reports/{id}</c>: the author fixes the report, while it is <c>Submitted</c>
/// or <c>Returned</c>, and it is <c>Submitted</c> again. Every line is replaced. The date can't change: a report
/// for another day is another report.
/// </summary>
/// <remarks>
/// The last review note is kept when a returned report is sent again, so the reviewer still sees what they asked for.
/// </remarks>
internal static class UpdateDailyReport
{
    internal sealed record Request(
        int? WorkersPresent,
        string? Weather,
        string? Issues,
        string? Notes,
        IReadOnlyList<DailyReportLineRequest?>? Lines,
        IReadOnlyList<DailyReportMaterialRequest?>? Materials,
        IReadOnlyList<DailyReportEquipmentRequest?>? Equipment,
        int? ReportRequestId,
        string? RowVersion) : IDailyReportRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/daily-reports/{reportId:int}", HandleAsync)
            .RequirePermission(SitePermissions.Module, PermissionAction.Create)
            .WithName("UpdateDailyReport")
            .WithSummary("Fix and resubmit your own daily report while it is Submitted or Returned");

    internal static async Task<Results<Ok<DailyReportView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int reportId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IEnumerable<IProjectRecordOwnership> owners,
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
        var fields = DailyReportFields.Validate(request, errors);
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

        if (project.WritesBlocked)
        {
            return ProjectProblems.CompletedProject();
        }

        var report = await db.DailyReports
            .Include(candidate => candidate.Lines)
            .Include(candidate => candidate.Materials)
            .Include(candidate => candidate.Equipment)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == reportId && candidate.ProjectId == projectId, cancellationToken);
        if (report is null)
        {
            return PlanningProblems.DailyReportNotFound();
        }

        switch (DailyReportRules.Edit(report.Status, report.AuthorUserId, caller.UserId))
        {
            case ReportDecision.NotAuthor:
                log.ActionRefused(caller.UserId, nameof(UpdateDailyReport), "not the author");
                return PlanningProblems.NotTheAuthor();
            case ReportDecision.WrongStatus:
                return WorkspaceProblems.WrongStatus("A reviewed daily report is final and can't be changed.");
        }

        var namedItems = report.Lines.Where(line => line.ProjectItemId is not null).Select(line => line.ProjectItemId!.Value).ToHashSet();
        await fields!.CheckReferencesAsync(db, owners, projectId, namedItems, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var oldStatus = report.Status;
        db.Entry(report).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        // The old rows go first, then the new ones are numbered from 1 again: two saves in one transaction, so the
        // unique (report, line number) index never sees both at once.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.DailyReportLines.RemoveRange(report.Lines);
        db.DailyReportMaterials.RemoveRange(report.Materials);
        db.DailyReportEquipment.RemoveRange(report.Equipment);
        report.Resubmit(clock.UtcNow);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        report.Lines.Clear();
        report.Materials.Clear();
        report.Equipment.Clear();
        fields.ApplyTo(report);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.DailyReportResubmitted(caller.UserId, projectId, report.Id, oldStatus.ToString());

        var view = await DailyReportView.LoadAsync(
            db, users, projectId, report.Id, caller.UserId, GetDailyReport.CanReview(httpContext, project), cancellationToken);
        return view is null ? PlanningProblems.DailyReportNotFound() : TypedResults.Ok(view);
    }
}
