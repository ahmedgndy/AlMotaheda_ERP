using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.HR.Data;

/// <summary>
/// An employee's request to HR (section 12.D): an attendance correction, a leave or permission, an overtime
/// clarification, a payroll question, a response to a case referred about them, or anything else. The employee
/// writes it from their own account (their login is linked to their employee record), HR reads, asks for more,
/// and decides. The employee never decides their own request and never sees another employee's. Nothing here
/// changes payroll: an approved request is a decision HR acts on through the payroll inputs, with their own rules.
/// </summary>
internal sealed class HrRequest
{
    public int Id { get; set; }

    /// <summary>Computed in the database: <c>HRQ-000001</c>.</summary>
    public string Number { get; private set; } = "";

    public int EmployeeId { get; set; }
    public Employee Employee { get; set; } = null!;

    public HrRequestType Type { get; set; }

    public string Subject { get; set; } = "";
    public string Details { get; set; } = "";

    /// <summary>The days a leave, permission or attendance correction is about.</summary>
    public DateOnly? FromDate { get; set; }
    public DateOnly? ToDate { get; set; }

    /// <summary>The HR referral this request answers, for a <see cref="HrRequestType.CaseResponse"/>.</summary>
    public int? ReferralId { get; set; }

    private HrRequestStatus _status = HrRequestStatus.Submitted;

    public HrRequestStatus Status { get => _status; init => _status = value; }

    public Guid IdempotencyKey { get; set; }

    public DateTime SubmittedAt { get; set; }

    /// <summary>The user who submitted it: the employee's own login.</summary>
    public int SubmittedBy { get; set; }

    public DateTime? ReviewStartedAt { get; set; }
    public int? ReviewedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<HrRequestMessage> Messages { get; } = [];

    public List<HrRequestAttachment> Attachments { get; } = [];

    private static readonly StatusFlow<HrRequestStatus> Flow = new(
        (HrRequestStatus.Submitted, HrRequestStatus.UnderReview),
        (HrRequestStatus.Submitted, HrRequestStatus.NeedsInfo),
        (HrRequestStatus.Submitted, HrRequestStatus.Approved),
        (HrRequestStatus.Submitted, HrRequestStatus.Rejected),
        (HrRequestStatus.Submitted, HrRequestStatus.Closed),
        (HrRequestStatus.UnderReview, HrRequestStatus.NeedsInfo),
        (HrRequestStatus.UnderReview, HrRequestStatus.Approved),
        (HrRequestStatus.UnderReview, HrRequestStatus.Rejected),
        (HrRequestStatus.UnderReview, HrRequestStatus.Closed),
        (HrRequestStatus.NeedsInfo, HrRequestStatus.Submitted),
        (HrRequestStatus.NeedsInfo, HrRequestStatus.Rejected),
        (HrRequestStatus.NeedsInfo, HrRequestStatus.Closed));

    public bool CanMoveTo(HrRequestStatus target) => Flow.Allows(_status, target);

    public bool IsOpen => _status is HrRequestStatus.Submitted or HrRequestStatus.UnderReview or HrRequestStatus.NeedsInfo;

    public void StartReview(int userId, DateTime now)
    {
        _status = Flow.Move(_status, HrRequestStatus.UnderReview);
        ReviewStartedAt = now;
        ReviewedBy = userId;
        UpdatedAt = now;
    }

    /// <summary>HR asks for more: the request goes back to the employee with the question as a message.</summary>
    public void AskForInfo(int userId, DateTime now)
    {
        _status = Flow.Move(_status, HrRequestStatus.NeedsInfo);
        ReviewedBy ??= userId;
        UpdatedAt = now;
    }

    /// <summary>The employee answers: back in HR's queue.</summary>
    public void Resubmit(DateTime now)
    {
        _status = Flow.Move(_status, HrRequestStatus.Submitted);
        UpdatedAt = now;
    }

    public void Decide(HrRequestStatus outcome, int userId, DateTime now, string note)
    {
        if (outcome is not (HrRequestStatus.Approved or HrRequestStatus.Rejected or HrRequestStatus.Closed))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A decision is Approved, Rejected or Closed.");
        }

        _status = Flow.Move(_status, outcome);
        DecidedAt = now;
        DecidedBy = userId;
        DecisionNote = note;
        UpdatedAt = now;
    }
}

/// <summary>Stored by name.</summary>
internal enum HrRequestType
{
    AttendanceCorrection,
    Leave,
    Permission,
    OvertimeClarification,
    PayrollQuestion,
    CaseResponse,
    Other,
}

/// <summary>Stored by name.</summary>
internal enum HrRequestStatus
{
    /// <summary>Waiting for HR (also after the employee answered a question).</summary>
    Submitted,
    UnderReview,

    /// <summary>HR asked the employee for more; waiting for the employee.</summary>
    NeedsInfo,
    Approved,
    Rejected,

    /// <summary>Answered or withdrawn: no approval was needed.</summary>
    Closed,
}

/// <summary>One message in the request's thread: the employee's or HR's. Append only.</summary>
internal sealed class HrRequestMessage
{
    public int Id { get; set; }

    public int RequestId { get; set; }
    public HrRequest Request { get; set; } = null!;

    public int AuthorUserId { get; set; }

    /// <summary>True when HR wrote it (the employee reads it as HR's answer).</summary>
    public bool FromHr { get; set; }

    public string Text { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A file uploaded as evidence for a request (a photo of a paper, a medical certificate), kept in HR's private
/// store; bound to the request by its save. Never replaced once bound: a new file is a new row.
/// </summary>
internal sealed class HrRequestAttachment
{
    public int Id { get; set; }

    public int? RequestId { get; set; }
    public HrRequest? Request { get; set; }

    public int? MessageId { get; set; }

    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";

    public Guid IdempotencyKey { get; set; }

    public DateTime UploadedAt { get; set; }
    public int UploadedBy { get; set; }
}

/// <summary>One row per HR user told of a new or answered request (the notification rule reads them).</summary>
internal sealed class HrRequestAlert
{
    public int Id { get; set; }

    public int RequestId { get; set; }
    public HrRequest Request { get; set; } = null!;

    public int RecipientUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
