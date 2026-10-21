using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.HR.Data;

/// <summary>
/// One stay of a salaried employee on a project, from a start date to an end date: a driver on a site, an
/// administrator, a supervisor. For employees who have no sign-in account (app users are on the project through
/// <c>projects.ProjectMember</c>; daily workers through <c>workforce.WorkerAssignment</c>). Ended rows are kept, so
/// "who was on the project on that day" can always be answered.
/// </summary>
internal sealed class EmployeeAssignment
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    /// <summary>The project (<c>projects.Project</c>, a foreign key added by hand in the migration).</summary>
    public int ProjectId { get; set; }

    public EmployeeAssignmentRole Role { get; set; }

    public DateOnly StartDate { get; set; }

    /// <summary>Empty while the employee is on the project.</summary>
    public DateOnly? EndDate { get; set; }

    public string? Notes { get; set; }

    public DateTime AssignedAt { get; set; }
    public int AssignedBy { get; set; }

    public DateTime? EndedAt { get; set; }
    public int? EndedBy { get; set; }
    public string? EndReason { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public bool IsActive => EndDate is null;

    /// <summary>On the project on <paramref name="day"/>.</summary>
    public bool Covers(DateOnly day) => StartDate <= day && (EndDate is null || EndDate >= day);
}

/// <summary>Stored by name.</summary>
internal enum EmployeeAssignmentRole
{
    SiteEngineer,
    Driver,
    Supervisor,
    Administrator,
    Guard,
    Other,
}

/// <summary>
/// A case referred to HR by another module: a rejected vehicle expense claim, a vehicle violation. HR investigates
/// and closes it with an outcome. A deduction is never automatic: HR raises it, if justified, as a
/// <see cref="PayrollInput"/> of kind Deduction through the ordinary payroll flow, which the salary sheet's review
/// then approves. Historical sheets are never touched.
/// </summary>
internal sealed class HrReferral
{
    public int Id { get; set; }

    /// <summary><c>HRR-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    /// <summary>One of <c>HrReferralSources</c>.</summary>
    public string SourceType { get; set; } = "";
    public int SourceId { get; set; }

    public int EmployeeId { get; set; }

    public int? VehicleId { get; set; }

    public string Subject { get; set; } = "";
    public string Details { get; set; } = "";

    /// <summary>The amount at stake when established; information for the investigation, never a deduction by itself.</summary>
    public decimal? Amount { get; set; }

    public string? ReferenceNo { get; set; }

    private HrReferralStatus _status = HrReferralStatus.Open;

    public HrReferralStatus Status { get => _status; init => _status = value; }

    public DateTime ReferredAt { get; set; }
    public int ReferredBy { get; set; }

    public DateTime? InvestigationStartedAt { get; set; }
    public int? InvestigatedBy { get; set; }
    public string? InvestigationNote { get; set; }

    public HrReferralOutcome? Outcome { get; set; }
    public string? ClosingNote { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int? ClosedBy { get; set; }

    /// <summary>The payroll deduction HR raised, when the outcome was a deduction.</summary>
    public int? DeductionInputId { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<HrReferralStatus> Flow = new(
        (HrReferralStatus.Open, HrReferralStatus.UnderInvestigation),
        (HrReferralStatus.Open, HrReferralStatus.Closed),
        (HrReferralStatus.UnderInvestigation, HrReferralStatus.Closed));

    public bool CanMoveTo(HrReferralStatus target) => Flow.Allows(_status, target);

    public void StartInvestigation(int userId, DateTime now, string? note)
    {
        _status = Flow.Move(_status, HrReferralStatus.UnderInvestigation);
        InvestigationStartedAt = now;
        InvestigatedBy = userId;
        InvestigationNote = note;
    }

    public void Close(HrReferralOutcome outcome, int userId, DateTime now, string note, int? deductionInputId)
    {
        _status = Flow.Move(_status, HrReferralStatus.Closed);
        Outcome = outcome;
        ClosingNote = note;
        ClosedAt = now;
        ClosedBy = userId;
        DeductionInputId = deductionInputId;
    }
}

/// <summary>Stored by name.</summary>
internal enum HrReferralStatus
{
    Open,
    UnderInvestigation,
    Closed,
}

/// <summary>Stored by name.</summary>
internal enum HrReferralOutcome
{
    /// <summary>Justified: a payroll deduction was raised through the ordinary flow.</summary>
    DeductionApplied,

    /// <summary>A written warning, no money.</summary>
    Warning,

    /// <summary>Not justified, or not the employee's fault.</summary>
    NoAction,
}
