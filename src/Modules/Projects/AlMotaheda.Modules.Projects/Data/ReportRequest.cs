namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// A request for one piece of information about one project, from one person to another ("طلب تقرير"), and the
/// answer to it. An engineer can also file a report nobody asked for (<see cref="IsProactive"/>).
/// </summary>
/// <remarks>
/// <para>The <see cref="RecipientUserId"/> is always the person who writes the answer. The
/// <see cref="RequesterUserId"/> is always the person the answer is for, who completes or returns it. For a
/// proactive report the author is the recipient and the person it is addressed to is the requester, so one state
/// machine serves both.</para>
/// <para>The answer is text only. Attachments are Archive documents linked to the request.</para>
/// </remarks>
internal sealed class ReportRequest
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The user the answer is for (Access module).</summary>
    public int RequesterUserId { get; set; }

    /// <summary>The user who answers (Access module).</summary>
    public int RecipientUserId { get; set; }

    public ReportRequestType Type { get; set; } = ReportRequestType.Other;

    public required string Title { get; set; }
    public string? Description { get; set; }

    public DateOnly? DueDate { get; set; }

    public ReportRequestPriority Priority { get; set; } = ReportRequestPriority.Normal;

    public ReportRequestStatus Status { get; set; } = ReportRequestStatus.Requested;

    /// <summary>True for a report filed without being asked. It is created already Submitted.</summary>
    public bool IsProactive { get; set; }

    /// <summary>The answer. Replaced when a returned answer is sent again.</summary>
    public string? ResponseText { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }

    /// <summary>The last time the answer was completed or returned, and by whom.</summary>
    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }

    /// <summary>Why the answer was last sent back. Kept after it is sent again.</summary>
    public string? ReturnNote { get; set; }

    public DateTime? CancelledAt { get; set; }
    public string? CancelNote { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>What the request is about. Stored by name.</summary>
internal enum ReportRequestType
{
    DailyReport,
    Labor,
    Materials,
    Subcontractor,
    Expense,
    Equipment,
    Other,
}

internal enum ReportRequestPriority
{
    Normal,
    High,
    Urgent,
}

/// <summary>Stored by name. The allowed moves between statuses are in <c>ReportRequestFlow</c>.</summary>
internal enum ReportRequestStatus
{
    /// <summary>Sent, not started yet.</summary>
    Requested,

    /// <summary>The recipient is working on it.</summary>
    InProgress,

    /// <summary>Answered. Waiting for the requester to complete or return it.</summary>
    Submitted,

    /// <summary>Sent back with a note. The recipient answers again.</summary>
    Returned,

    /// <summary>The answer was accepted. Final.</summary>
    Completed,

    /// <summary>Withdrawn by the requester, with a note. Final.</summary>
    Cancelled,
}
