namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>
/// One engineer's labor sheet for one project, day and shift: who worked, how long, on what. It becomes labor cost
/// only when someone else approves it. One sheet per engineer, project, day and shift (decision W1).
/// </summary>
internal sealed class DailySheet
{
    public int Id { get; set; }

    /// <summary>The project (<c>projects.Project</c>, a foreign key added by hand in the migration).</summary>
    public int ProjectId { get; set; }

    public DateOnly WorkDate { get; set; }

    public Shift Shift { get; set; }

    /// <summary>The engineer who recorded the sheet. Only they edit and submit it; never they approve it.</summary>
    public int RecordedBy { get; set; }

    public SheetStatus Status { get; set; } = SheetStatus.Draft;

    public DateTime? SubmittedAt { get; set; }

    /// <summary>The last review (return, reject or approve). Kept after a resubmit, so the return note stays visible.</summary>
    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Set on every change to the sheet or its lines, so the sheet's row version moves with its lines.</summary>
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<AttendanceLine> Lines { get; } = [];
}

/// <summary>Stored by name. A worker can be recorded once per shift per day: a night guard can also work the day.</summary>
internal enum Shift
{
    Day,
    Night,
}

/// <summary>
/// Stored by name. <c>Draft</c> → <c>Submitted</c> → <c>Approved</c> (cost), <c>Returned</c> (back to the recorder)
/// or <c>Rejected</c> (final; the lines become void).
/// </summary>
internal enum SheetStatus
{
    Draft,
    Submitted,
    Returned,
    Approved,
    Rejected,
}

/// <summary>
/// One worker's day (or half day) on a sheet. The pay columns stay <c>NULL</c> until the sheet is approved; then
/// the rate and every amount are copied onto the line and never change.
/// </summary>
/// <remarks>
/// <see cref="WorkDate"/> and <see cref="Shift"/> are copied from the sheet so a unique index can make sure a worker
/// is recorded once per day and shift, whatever the project or the engineer.
/// </remarks>
internal sealed class AttendanceLine
{
    public int Id { get; set; }

    public int SheetId { get; set; }
    public DailySheet Sheet { get; set; } = null!;

    public int WorkerId { get; set; }
    public Worker Worker { get; set; } = null!;

    /// <summary>The trade worked that day; usually the worker's own trade.</summary>
    public int TradeId { get; set; }
    public Trade Trade { get; set; } = null!;

    public DateOnly WorkDate { get; set; }

    public Shift Shift { get; set; }

    /// <summary>1 (a full day) or 0.5 (half a day).</summary>
    public decimal Days { get; set; }

    public decimal OvertimeHours { get; set; }

    /// <summary>One of <c>WorkTypes</c>.</summary>
    public string WorkType { get; set; } = "";

    public string? Notes { get; set; }

    /// <summary>True once the sheet is rejected: the line counts nowhere and the worker-day is free again.</summary>
    public bool IsVoid { get; set; }

    public decimal? AppliedDailyRate { get; set; }
    public decimal? BaseAmount { get; set; }
    public decimal? OvertimeAmount { get; set; }
    public decimal? AllowancesAmount { get; set; }
    public decimal? DeductionsAmount { get; set; }

    /// <summary>base + overtime + allowances − deductions, never below zero. The recognized cost of the line.</summary>
    public decimal? NetAmount { get; set; }

    public List<LineAdjustment> Adjustments { get; } = [];

    public List<LaborAllocation> Allocations { get; } = [];
}

/// <summary>An allowance added to, or a deduction taken from, one line's pay. A deduction needs a reason.</summary>
internal sealed class LineAdjustment
{
    public int Id { get; set; }

    public int LineId { get; set; }
    public AttendanceLine Line { get; set; } = null!;

    public AdjustmentKind Kind { get; set; }

    public AdjustmentType Type { get; set; }

    public decimal Amount { get; set; }

    public string? Reason { get; set; }
}

internal enum AdjustmentKind
{
    Allowance,
    Deduction,
}

/// <summary>The prototype's six allowance kinds; a deduction uses <c>Other</c> or the closest one.</summary>
internal enum AdjustmentType
{
    Food,
    Transport,
    Night,
    Accommodation,
    Tip,
    Other,
}

/// <summary>
/// "This portion of this worker's day went to this project item." The portions of one line add up to at most 1,
/// so a day can never be counted in full against two items. The rest of a line is "not linked to an item".
/// </summary>
internal sealed class LaborAllocation
{
    public int Id { get; set; }

    public int LineId { get; set; }
    public AttendanceLine Line { get; set; } = null!;

    /// <summary>The project item (<c>projects.ProjectItem</c>, a foreign key added by hand in the migration).</summary>
    public int ProjectItemId { get; set; }

    /// <summary>Between 0 (excluded) and 1, with four decimals.</summary>
    public decimal Portion { get; set; }

    /// <summary>The part of the line's net on this item. Set when the sheet is approved, recomputed when an approved line is re-allocated.</summary>
    public decimal? Amount { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
}
