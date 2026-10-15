using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;

namespace AlMotaheda.Modules.Projects.Features.DailyReports;

/// <summary>
/// The permission module of daily site reports: View to read, Create to write and fix your own, Approve (with the
/// Executive role or the project's manager) to review or return.
/// </summary>
internal static class SitePermissions
{
    public const string Module = "site";
}

/// <summary>The answer to "may this caller do this to this report?", with the reason when not.</summary>
internal enum ReportDecision
{
    Allowed,

    /// <summary>The caller wrote the report, so someone else must review it (409 <c>own_record</c>).</summary>
    OwnReport,

    /// <summary>Only the author may edit (403).</summary>
    NotAuthor,

    /// <summary>The report is not in a status that allows this (409 <c>invalid_status</c>).</summary>
    WrongStatus,
}

/// <summary>
/// The daily report workflow (02-projects-planning.md, section C), with no database access:
/// Submitted → Reviewed (final) or Returned by a reviewer; Submitted or Returned → Submitted again by the author.
/// </summary>
internal static class DailyReportRules
{
    /// <summary>How far back a report can be dated: today or one of the 7 days before.</summary>
    public const int MaxDaysBack = 7;

    /// <summary>The error for a report date, or <c>null</c> when it is today or one of the 7 days before.</summary>
    public static string? DateProblem(DateOnly date, DateOnly today) =>
        date > today ? "The report date can't be in the future."
        : date < today.AddDays(-MaxDaysBack) ? $"The report date can be at most {MaxDaysBack} days ago."
        : null;

    /// <summary>Edit and resubmit: only the author, only while Submitted or Returned.</summary>
    public static ReportDecision Edit(DailyReportStatus status, int authorUserId, int callerUserId)
    {
        if (authorUserId != callerUserId)
        {
            return ReportDecision.NotAuthor;
        }

        return status is DailyReportStatus.Submitted or DailyReportStatus.Returned ? ReportDecision.Allowed : ReportDecision.WrongStatus;
    }

    /// <summary>Review or return: never the author, only from Submitted. Reviewed is final.</summary>
    public static ReportDecision Review(DailyReportStatus status, int authorUserId, int callerUserId)
    {
        if (authorUserId == callerUserId)
        {
            return ReportDecision.OwnReport;
        }

        return status == DailyReportStatus.Submitted ? ReportDecision.Allowed : ReportDecision.WrongStatus;
    }

    /// <summary>Reviewers are an Executive or the project's manager (site Approve is checked by the endpoint).</summary>
    public static bool CanReview(ProjectAccessInfo project) => project.CallerIsExecutive || project.CallerIsManager;
}
