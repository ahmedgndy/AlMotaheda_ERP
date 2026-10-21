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
/// <para>Offline sending: the app may send an <c>idempotencyKey</c> (a GUID it made for this report) and
/// <c>writtenAt</c> (when the report was written on the phone). The same author and key again return the first
/// report with 200 and store nothing, so a retry after a lost answer never makes a second report; the same key on
/// another project or day answers 409 <c>idempotency_key_reused</c>.</para>
/// </remarks>
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
        int? ReportRequestId,
        string? IdempotencyKey = null,
        DateTime? WrittenAt = null) : IDailyReportRequest;

    /// <summary>A report written offline can wait on the phone this long; older times are refused as wrong clocks.</summary>
    private static readonly TimeSpan MaxWrittenAge = TimeSpan.FromDays(30);

    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/daily-reports", HandleAsync)
            .RequirePermission(SitePermissions.Module, PermissionAction.Create)
            .WithName("CreateDailyReport")
            .WithSummary("Submit a daily site report for today or one of the 7 days before (one per author, project and day)");

    internal static async Task<Results<Created<DailyReportView>, Ok<DailyReportView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
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

        Guid? idempotencyKey = null;
        if (Input.Clean(request.IdempotencyKey) is { } key)
        {
            if (Guid.TryParse(key, out var parsed) && parsed != Guid.Empty)
            {
                idempotencyKey = parsed;
            }
            else
            {
                errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the app for this report.");
            }
        }

        // A repeat of a report already stored (its answer was lost) gets that report back before any rule that can
        // change with time: by now the day may be more than 7 days ago, or the project completed.
        ProjectAccessInfo? project = null;
        if (!errors.HasErrors && idempotencyKey is { } repeatKey)
        {
            project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
            if (project is null)
            {
                return ProjectProblems.ProjectNotFound();
            }

            if (await RepeatAsync(db, users, httpContext, project, projectId, caller.UserId, repeatKey, reportDate!.Value, cancellationToken) is { } repeat)
            {
                return repeat;
            }
        }

        if (reportDate is { } date && DailyReportRules.DateProblem(date, clock.Today) is { } dateProblem)
        {
            errors.Add("reportDate", dateProblem);
        }

        var fields = DailyReportFields.Validate(request, errors);

        var now = clock.UtcNow;
        var writtenAt = request.WrittenAt?.ToUniversalTime();
        if (writtenAt is { } written && (written > now + MaxClockSkew || written < now - MaxWrittenAge))
        {
            errors.Add("writtenAt", "writtenAt must be within the last 30 days.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        project ??= await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
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

        var report = new DailyReport
        {
            ProjectId = projectId,
            ReportDate = reportDate!.Value,
            AuthorUserId = caller.UserId,
            Status = DailyReportStatus.Submitted,
            CreatedAt = now,
            SubmittedAt = now,
            IdempotencyKey = idempotencyKey,
            WrittenAt = writtenAt,
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
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, DailyReportConfiguration.IdempotencyKeyIndex))
        {
            // The same offline send arrived twice at the same moment: answer with the one that was stored.
            db.ChangeTracker.Clear();
            return await RepeatAsync(db, users, httpContext, project, projectId, caller.UserId, idempotencyKey!.Value, report.ReportDate, cancellationToken)
                   ?? PlanningProblems.ReportKeyUsedElsewhere();
        }

        log.DailyReportSubmitted(caller.UserId, projectId, report.Id);

        var view = await DailyReportView.LoadAsync(
            db, users, projectId, report.Id, caller.UserId, GetDailyReport.CanReview(httpContext, project), cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/daily-reports/{report.Id}", view!);
    }

    /// <summary>
    /// The report this author already stored with this key: 200 with it when it is the same project and day, 409
    /// <c>idempotency_key_reused</c> when the key was used for another report, <c>null</c> when the key is new.
    /// </summary>
    private static async Task<Results<Created<DailyReportView>, Ok<DailyReportView>, ValidationProblem, ProblemHttpResult>?> RepeatAsync(
        ProjectsDbContext db,
        IUserDirectory users,
        HttpContext httpContext,
        ProjectAccessInfo project,
        int projectId,
        int authorUserId,
        Guid idempotencyKey,
        DateOnly reportDate,
        CancellationToken cancellationToken)
    {
        var first = await db.DailyReports.AsNoTracking()
            .Where(report => report.AuthorUserId == authorUserId && report.IdempotencyKey == idempotencyKey)
            .Select(report => new { report.Id, report.ProjectId, report.ReportDate })
            .FirstOrDefaultAsync(cancellationToken);
        if (first is null)
        {
            return null;
        }

        if (first.ProjectId != projectId || first.ReportDate != reportDate)
        {
            return PlanningProblems.ReportKeyUsedElsewhere();
        }

        var view = await DailyReportView.LoadAsync(
            db, users, projectId, first.Id, authorUserId, GetDailyReport.CanReview(httpContext, project), cancellationToken);
        return TypedResults.Ok(view!);
    }
}
