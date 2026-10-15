using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.ReportRequests;

/// <summary>One report request, flat.</summary>
internal sealed record ReportRequestRow(
    int Id,
    int ProjectId,
    int RequesterUserId,
    int RecipientUserId,
    ReportRequestType Type,
    string Title,
    string? Description,
    DateOnly? DueDate,
    ReportRequestPriority Priority,
    ReportRequestStatus Status,
    bool IsProactive,
    string? ResponseText,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? SubmittedAt,
    DateTime? ReviewedAt,
    int? ReviewedBy,
    string? ReturnNote,
    DateTime? CancelledAt,
    string? CancelNote,
    byte[] RowVersion);

/// <summary>A daily report written in answer to a request.</summary>
internal sealed record LinkedDailyReportRow(int Id, DateOnly ReportDate, DailyReportStatus Status, int AuthorUserId);

/// <summary>Who is asking, for the visibility filter.</summary>
/// <param name="SeesEveryRequest">An Executive, or a Manager on a project they can see.</param>
internal sealed record RequestViewer(int UserId, bool SeesEveryRequest);

/// <summary>The filters of <c>GET /api/projects/{pid}/report-requests</c>, after validation.</summary>
/// <param name="Mine">Only requests the caller sent or received.</param>
internal sealed record ReportRequestFilter(ReportRequestStatus? Status, bool Mine);

/// <summary>The report request queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class ReportRequestQueries
{
    /// <summary>The requests of the project the viewer may see, newest first. Every filter runs in SQL.</summary>
    public static IQueryable<ReportRequestRow> Filtered(ProjectsDbContext db, int projectId, RequestViewer viewer, ReportRequestFilter filter)
    {
        var query = Visible(db, projectId, viewer);

        if (filter.Status is { } status)
        {
            query = query.Where(request => request.Status == status);
        }

        if (filter.Mine)
        {
            query = query.Where(request => request.RequesterUserId == viewer.UserId || request.RecipientUserId == viewer.UserId);
        }

        return Project(query.OrderByDescending(request => request.CreatedAt).ThenByDescending(request => request.Id));
    }

    /// <summary>One request, or none when it is not on this project or the viewer may not see it.</summary>
    public static IQueryable<ReportRequestRow> One(ProjectsDbContext db, int projectId, int requestId, RequestViewer viewer) =>
        Project(Visible(db, projectId, viewer).Where(request => request.Id == requestId));

    /// <summary>The daily reports that answer a request, newest day first.</summary>
    public static IQueryable<LinkedDailyReportRow> LinkedReports(ProjectsDbContext db, int projectId, int requestId) =>
        db.DailyReports.AsNoTracking()
            .Where(report => report.ProjectId == projectId && report.ReportRequestId == requestId)
            .OrderByDescending(report => report.ReportDate)
            .ThenByDescending(report => report.Id)
            .Select(report => new LinkedDailyReportRow(report.Id, report.ReportDate, report.Status, report.AuthorUserId));

    /// <summary>
    /// The visibility rule in SQL: everything for an Executive or a Manager; otherwise only what the viewer sent
    /// or received. The project scope itself is checked before, by <c>IProjectAccess</c>.
    /// </summary>
    private static IQueryable<ReportRequest> Visible(ProjectsDbContext db, int projectId, RequestViewer viewer)
    {
        var query = db.ReportRequests.AsNoTracking().Where(request => request.ProjectId == projectId);
        return viewer.SeesEveryRequest
            ? query
            : query.Where(request => request.RequesterUserId == viewer.UserId || request.RecipientUserId == viewer.UserId);
    }

    private static IQueryable<ReportRequestRow> Project(IQueryable<ReportRequest> query) =>
        query.Select(request => new ReportRequestRow(
            request.Id,
            request.ProjectId,
            request.RequesterUserId,
            request.RecipientUserId,
            request.Type,
            request.Title,
            request.Description,
            request.DueDate,
            request.Priority,
            request.Status,
            request.IsProactive,
            request.ResponseText,
            request.CreatedAt,
            request.StartedAt,
            request.SubmittedAt,
            request.ReviewedAt,
            request.ReviewedBy,
            request.ReturnNote,
            request.CancelledAt,
            request.CancelNote,
            request.RowVersion));
}
