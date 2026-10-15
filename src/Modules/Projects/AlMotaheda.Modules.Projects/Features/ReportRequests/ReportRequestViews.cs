using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Projects;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.ReportRequests;

/// <summary>A daily report that answers the request.</summary>
internal sealed record LinkedDailyReportView(int Id, DateOnly ReportDate, string Status, PersonRef Author);

/// <summary>The moves the caller may make now, so the frontend shows only the buttons that will work.</summary>
internal sealed record ReportRequestAbilities(bool Start, bool Submit, bool Complete, bool Return, bool Cancel);

/// <summary>
/// One report request. <see cref="LinkedDailyReports"/> is filled by the detail and the writes, and is <c>null</c>
/// in the list. Attachments are Archive documents linked to the request.
/// </summary>
internal sealed record ReportRequestView(
    int Id,
    int ProjectId,
    string Type,
    string Title,
    string? Description,
    DateOnly? DueDate,
    string Priority,
    string Status,
    bool IsProactive,
    bool IsOverdue,
    PersonRef Requester,
    PersonRef Recipient,
    string? ResponseText,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? SubmittedAt,
    DateTime? ReviewedAt,
    PersonRef? Reviewer,
    string? ReturnNote,
    DateTime? CancelledAt,
    string? CancelNote,
    string RowVersion,
    ReportRequestAbilities Can,
    IReadOnlyList<LinkedDailyReportView>? LinkedDailyReports)
{
    public static ReportRequestView From(
        ReportRequestRow row,
        ProjectCaller caller,
        bool callerMayApprove,
        DateOnly today,
        IReadOnlyDictionary<int, UserSummary> people,
        IReadOnlyList<LinkedDailyReportView>? linked)
    {
        bool May(RequestAction action) =>
            ReportRequestFlow.Decide(action, row.Status, row.RequesterUserId, row.RecipientUserId, caller.UserId, caller.IsExecutive)
            == RequestDecision.Allowed;

        return new ReportRequestView(
            row.Id,
            row.ProjectId,
            row.Type.ToString(),
            row.Title,
            row.Description,
            row.DueDate,
            row.Priority.ToString(),
            row.Status.ToString(),
            row.IsProactive,
            ReportRequestFlow.IsOverdue(row.Status, row.DueDate, today),
            PersonRef.From(row.RequesterUserId, people),
            PersonRef.From(row.RecipientUserId, people),
            row.ResponseText,
            row.CreatedAt,
            row.StartedAt,
            row.SubmittedAt,
            row.ReviewedAt,
            row.ReviewedBy is { } reviewer ? PersonRef.From(reviewer, people) : null,
            row.ReturnNote,
            row.CancelledAt,
            row.CancelNote,
            RowVersions.Format(row.RowVersion),
            new ReportRequestAbilities(
                May(RequestAction.Start),
                May(RequestAction.Submit),
                callerMayApprove && May(RequestAction.Complete),
                callerMayApprove && May(RequestAction.Return),
                May(RequestAction.Cancel)),
            linked);
    }

    /// <summary>The people named on the rows, for one name lookup.</summary>
    public static IEnumerable<int> People(IEnumerable<ReportRequestRow> rows) =>
        rows.SelectMany(row => row.ReviewedBy is { } reviewer
            ? new[] { row.RequesterUserId, row.RecipientUserId, reviewer }
            : [row.RequesterUserId, row.RecipientUserId]);

    /// <summary>Reads one request the caller may see, with its linked daily reports, or <c>null</c>.</summary>
    public static async Task<ReportRequestView?> LoadAsync(
        ProjectsDbContext db,
        IUserDirectory users,
        ProjectCaller caller,
        int projectId,
        int requestId,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var row = await ReportRequestQueries.One(db, projectId, requestId, Viewer(caller)).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var linked = await ReportRequestQueries.LinkedReports(db, projectId, requestId).ToListAsync(cancellationToken);
        var people = await PlanningPeople.FindAsync(users, People([row]).Concat(linked.Select(report => report.AuthorUserId)), cancellationToken);

        return From(
            row,
            caller,
            caller.Principal.HasPermission(ReportPermissions.Module, PermissionAction.Approve),
            today,
            people,
            linked.Select(report => new LinkedDailyReportView(
                report.Id, report.ReportDate, report.Status.ToString(), PersonRef.From(report.AuthorUserId, people))).ToList());
    }

    public static RequestViewer Viewer(ProjectCaller caller) => new(caller.UserId, ReportRequestFlow.SeesEveryRequest(caller.Role));
}

/// <summary>Who can take part in a request on a project.</summary>
internal static class ReportRequestParties
{
    /// <summary>
    /// True when the user exists, is active, and can see the project right now: a company-wide role (Executive,
    /// Accountant), or an active member of the project. Nobody is asked about a project they can't open.
    /// </summary>
    public static async Task<bool> CanTakePartAsync(
        ProjectsDbContext db, IUserDirectory users, int projectId, int userId, CancellationToken cancellationToken)
    {
        var found = await users.FindAsync([userId], cancellationToken);
        if (!found.TryGetValue(userId, out var user) || !user.IsActive)
        {
            return false;
        }

        return ProjectScopes.IsCompanyWide(user.RoleCode)
            || await db.ProjectMembers.AsNoTracking()
                .AnyAsync(member => member.ProjectId == projectId && member.UserId == userId && member.EndDate == null, cancellationToken);
    }
}
