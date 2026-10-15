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

/// <summary>
/// <c>POST /api/projects/{pid}/daily-reports</c>: write what was done on site on one day. It is saved as
/// <c>Submitted</c>, waiting for review. One report per author, project and day (409 <c>report_exists</c>).
/// </summary>
/// <remarks>
/// A daily report holds no money and creates no cost: labor is paid from the labor sheets (Workforce) and
/// subcontract work is measured in Subcontractors (decision D2).
/// </remarks>
internal static class CreateDailyReport
{
    internal sealed record Request(
        string? ReportDate,
        int? WorkersPresent,
        string? Weather,
        string? Issues,
        string? Notes,
        IReadOnlyList<DailyReportLineRequest?>? Lines,
        IReadOnlyList<DailyReportMaterialRequest?>? Materials,
        IReadOnlyList<DailyReportEquipmentRequest?>? Equipment,
        int? ReportRequestId) : IDailyReportRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/daily-reports", HandleAsync)
            .RequirePermission(SitePermissions.Module, PermissionAction.Create)
            .WithName("CreateDailyReport")
            .WithSummary("Submit a daily site report for today or one of the 7 days before (one per author, project and day)");

    internal static async Task<Results<Created<DailyReportView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
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
        var reportDate = Input.RequiredDate(request.ReportDate, errors, "reportDate", "The report date");
        if (reportDate is { } date && DailyReportRules.DateProblem(date, clock.Today) is { } dateProblem)
        {
            errors.Add("reportDate", dateProblem);
        }

        var fields = DailyReportFields.Validate(request, errors);
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

        await fields!.CheckReferencesAsync(db, owners, projectId, itemsAlreadyNamed: [], errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await db.DailyReports.AnyAsync(
                report => report.ProjectId == projectId && report.ReportDate == reportDate && report.AuthorUserId == caller.UserId,
                cancellationToken))
        {
            return PlanningProblems.ReportAlreadyFiled();
        }

        var now = clock.UtcNow;
        var report = new DailyReport
        {
            ProjectId = projectId,
            ReportDate = reportDate!.Value,
            AuthorUserId = caller.UserId,
            Status = DailyReportStatus.Submitted,
            CreatedAt = now,
            SubmittedAt = now,
        };
        fields.ApplyTo(report);
        db.DailyReports.Add(report);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, DailyReportConfiguration.AuthorDayIndex))
        {
            return PlanningProblems.ReportAlreadyFiled();
        }

        log.DailyReportSubmitted(caller.UserId, projectId, report.Id);

        var view = await DailyReportView.LoadAsync(
            db, users, projectId, report.Id, caller.UserId, GetDailyReport.CanReview(httpContext, project), cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/daily-reports/{report.Id}", view!);
    }
}
