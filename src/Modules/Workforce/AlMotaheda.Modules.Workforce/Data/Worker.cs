namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>
/// A daily-paid worker. Created once (a national id is unique) and reused on every project. A worker never signs
/// in: the site engineer records the worker's days on a <see cref="DailySheet"/>.
/// </summary>
internal sealed class Worker
{
    public int Id { get; set; }

    public string NameAr { get; set; } = "";

    public string NameEn { get; set; } = "";

    /// <summary>The 14-digit Egyptian national id, or <c>null</c> when not known yet. Unique when present.</summary>
    public string? NationalId { get; set; }

    public string? Phone { get; set; }

    /// <summary>The worker's own trade.</summary>
    public int TradeId { get; set; }
    public Trade Trade { get; set; } = null!;

    /// <summary>The worker's own daily rate; <c>null</c> means the trade's default rate.</summary>
    public decimal? DailyRate { get; set; }

    public WorkerStatus Status { get; set; } = WorkerStatus.Active;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Stored by name. An inactive worker keeps the history but can't be recorded on a new sheet.</summary>
internal enum WorkerStatus
{
    Active,
    Inactive,
}

/// <summary>
/// A worker on one project for a period. Ended rows are kept, so the history of who worked where never changes.
/// A worker has at most one open assignment per project.
/// </summary>
internal sealed class WorkerAssignment
{
    public int Id { get; set; }

    public int WorkerId { get; set; }
    public Worker Worker { get; set; } = null!;

    /// <summary>The project (<c>projects.Project</c>, a foreign key added by hand in the migration).</summary>
    public int ProjectId { get; set; }

    /// <summary>A rate for this project only; <c>null</c> means the worker's own rate.</summary>
    public decimal? DailyRate { get; set; }

    /// <summary>The first day on the project.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>The last day on the project (inclusive); <c>null</c> while the worker is still on it.</summary>
    public DateOnly? EndDate { get; set; }

    public DateTime AssignedAt { get; set; }
    public int AssignedBy { get; set; }

    public DateTime? EndedAt { get; set; }
    public int? EndedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    /// <summary>True when the worker is on the project on <paramref name="date"/>.</summary>
    public bool Covers(DateOnly date) => StartDate <= date && (EndDate is null || EndDate >= date);
}
