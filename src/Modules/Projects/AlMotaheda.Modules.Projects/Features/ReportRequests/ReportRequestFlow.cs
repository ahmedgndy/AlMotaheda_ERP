using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Data;

namespace AlMotaheda.Modules.Projects.Features.ReportRequests;

/// <summary>
/// The permission module of report requests: View to read and to work on what was sent to you, Create to file a
/// report nobody asked for, Edit to ask someone for a report, Approve to complete or return an answer.
/// </summary>
internal static class ReportPermissions
{
    public const string Module = "reports";
}

/// <summary>The moves of a report request after it is created.</summary>
internal enum RequestAction
{
    Start,
    Submit,
    Complete,
    Return,
    Cancel,
}

/// <summary>The answer to "may this caller make this move now?", with the reason when not.</summary>
internal enum RequestDecision
{
    Allowed,

    /// <summary>Only the recipient starts and answers (403).</summary>
    NotRecipient,

    /// <summary>Only the requester cancels (403).</summary>
    NotRequester,

    /// <summary>Only the requester, or an Executive, completes or returns the answer (403).</summary>
    NotRequesterOrExecutive,

    /// <summary>The request is not in a status that allows this move (409 <c>invalid_status</c>).</summary>
    WrongStatus,
}

/// <summary>
/// The report request state machine (02-projects-planning.md, section D), with no database access:
/// <code>
/// Requested ─start─► InProgress ─submit─► Submitted ─complete─► Completed
///      └──────────── submit ───────────────┘   │
///                                              └─return (note)─► Returned ─submit─► Submitted
/// Requested / InProgress / Returned ─cancel (note, requester only)─► Cancelled
/// Proactive report: created as Submitted by its author (who is the recipient)
/// </code>
/// </summary>
internal static class ReportRequestFlow
{
    /// <summary>The status each move leads to.</summary>
    public static ReportRequestStatus Target(RequestAction action) => action switch
    {
        RequestAction.Start => ReportRequestStatus.InProgress,
        RequestAction.Submit => ReportRequestStatus.Submitted,
        RequestAction.Complete => ReportRequestStatus.Completed,
        RequestAction.Return => ReportRequestStatus.Returned,
        RequestAction.Cancel => ReportRequestStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown move."),
    };

    /// <summary>Open = still waiting for an answer or a fix.</summary>
    public static bool IsOpen(ReportRequestStatus status) =>
        status is ReportRequestStatus.Requested or ReportRequestStatus.InProgress or ReportRequestStatus.Returned;

    /// <summary>Who may make the move (first) and from which status (second).</summary>
    public static RequestDecision Decide(
        RequestAction action, ReportRequestStatus status, int requesterUserId, int recipientUserId, int callerUserId, bool callerIsExecutive)
    {
        var (allowedCaller, refusal) = action switch
        {
            RequestAction.Start or RequestAction.Submit => (callerUserId == recipientUserId, RequestDecision.NotRecipient),
            RequestAction.Cancel => (callerUserId == requesterUserId, RequestDecision.NotRequester),
            _ => (callerUserId == requesterUserId || callerIsExecutive, RequestDecision.NotRequesterOrExecutive),
        };

        if (!allowedCaller)
        {
            return refusal;
        }

        var allowedFrom = action switch
        {
            RequestAction.Start => status == ReportRequestStatus.Requested,
            RequestAction.Submit or RequestAction.Cancel => IsOpen(status),
            _ => status == ReportRequestStatus.Submitted,
        };

        return allowedFrom ? RequestDecision.Allowed : RequestDecision.WrongStatus;
    }

    /// <summary>
    /// Who sees a request: an Executive and a Manager (on projects they can see) see every request of the project;
    /// anyone else only the requests they sent or received.
    /// </summary>
    public static bool SeesEveryRequest(string role) => role is AccessRoles.Executive or AccessRoles.Manager;

    /// <summary>A request is late when it is still open after its due date.</summary>
    public static bool IsOverdue(ReportRequestStatus status, DateOnly? dueDate, DateOnly today) =>
        IsOpen(status) && dueDate is { } due && due < today;
}
