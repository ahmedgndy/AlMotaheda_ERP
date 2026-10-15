namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// What was done on a project's site on one day, as written by one person ("التقرير اليومي").
/// </summary>
/// <remarks>
/// <para><b>No money.</b> A daily report is a description of the day. It is not the labor sheet (paid attendance
/// lives in Workforce) and not measured subcontract work (that lives in Subcontractors). Reviewing a report never
/// creates a cost anywhere (decision D2).</para>
/// <para>One report per author, project and day (unique index).</para>
/// <para>Materials received that day are not stored here: the report detail lists the project's supplies dated
/// that day. Photos are Archive documents linked to the report.</para>
/// </remarks>
internal sealed class DailyReport
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The day the report describes (a business date at the company).</summary>
    public DateOnly ReportDate { get; set; }

    /// <summary>The user who wrote the report (Access module). Only they can edit it; they can never review it.</summary>
    public int AuthorUserId { get; set; }

    public DailyReportStatus Status { get; set; } = DailyReportStatus.Submitted;

    /// <summary>How many workers were on site, as the author counted them. Information only: nobody is paid from it.</summary>
    public int? WorkersPresent { get; set; }

    public string? Weather { get; set; }

    /// <summary>Problems and delays of the day.</summary>
    public string? Issues { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The last time the author sent the report: on creation, and again after each edit.</summary>
    public DateTime SubmittedAt { get; set; }

    /// <summary>
    /// The last review or return. Kept when a returned report is sent again, so the reviewer still sees what they
    /// asked for.
    /// </summary>
    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    /// <summary>Optional: the report request this report answers. Same project.</summary>
    public int? ReportRequestId { get; set; }
    public ReportRequest? ReportRequest { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<DailyReportLine> Lines { get; } = [];
    public List<DailyReportMaterial> Materials { get; } = [];
    public List<DailyReportEquipment> Equipment { get; } = [];
}

/// <summary>Stored by name.</summary>
internal enum DailyReportStatus
{
    /// <summary>Waiting for review. The author can still edit it.</summary>
    Submitted,

    /// <summary>Accepted by the reviewer. Final.</summary>
    Reviewed,

    /// <summary>Sent back with a note. The author fixes it and it becomes Submitted again.</summary>
    Returned,
}

/// <summary>One piece of work done that day: what, how much, and by whom.</summary>
internal sealed class DailyReportLine
{
    public int Id { get; set; }

    public int ReportId { get; set; }
    public DailyReport Report { get; set; } = null!;

    /// <summary>1, 2, 3… within the report.</summary>
    public int LineNo { get; set; }

    /// <summary>One of <c>WorkTypes</c> (Projects.Contracts).</summary>
    public required string WorkType { get; set; }

    public required string Description { get; set; }

    /// <summary>One of <c>MeasurementUnits</c>.</summary>
    public required string Unit { get; set; }

    public decimal Quantity { get; set; }

    public WorkPerformer PerformedBy { get; set; }

    /// <summary>Optional: the project item the work belongs to. Same project as the report.</summary>
    public int? ProjectItemId { get; set; }
    public ProjectItem? ProjectItem { get; set; }

    /// <summary>Subcontractor lines only: the company's name as typed.</summary>
    public string? SubcontractorName { get; set; }

    /// <summary>
    /// Subcontractor lines only: the id of the subcontract. A reference by id with no foreign key, because the
    /// contract belongs to the Subcontractors module.
    /// </summary>
    public int? SubContractId { get; set; }

    /// <summary>How many people did this work. Information only.</summary>
    public int? WorkersCount { get; set; }
}

/// <summary>Who did the work on a daily report line. Stored by name.</summary>
internal enum WorkPerformer
{
    /// <summary>The company's own workers.</summary>
    Internal,
    Subcontractor,
}

/// <summary>A material used on site that day (not a purchase: purchases are supplies).</summary>
internal sealed class DailyReportMaterial
{
    public int Id { get; set; }

    public int ReportId { get; set; }
    public DailyReport Report { get; set; } = null!;

    public required string MaterialName { get; set; }

    /// <summary>One of <c>MeasurementUnits</c>.</summary>
    public required string Unit { get; set; }

    public decimal Quantity { get; set; }
}

/// <summary>A piece of equipment used on site that day. No cost: equipment costs live in the Fleet module.</summary>
internal sealed class DailyReportEquipment
{
    public int Id { get; set; }

    public int ReportId { get; set; }
    public DailyReport Report { get; set; } = null!;

    /// <summary>
    /// Optional: the id of the equipment in the register. A reference by id with no foreign key, because the
    /// register belongs to the Fleet module.
    /// </summary>
    public int? EquipmentId { get; set; }

    /// <summary>The equipment's name as typed (or copied from the register by the client).</summary>
    public required string EquipmentName { get; set; }

    /// <summary>Hours worked that day, with one decimal.</summary>
    public decimal? Hours { get; set; }
}
