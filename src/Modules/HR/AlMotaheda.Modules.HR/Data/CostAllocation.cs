using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.HR.Data;

/// <summary>
/// The company's rule for charging salaries to projects (one row, id 1; docs/cost-flow §2.6). Not set by default:
/// the company decides how many working days a month has, and allocation is refused until it does.
/// </summary>
internal sealed class CostAllocationPolicy
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// The working days of a month: a project's share of a monthly salary is salary ÷ this × the approved days.
    /// <c>null</c> until the Executive sets it.
    /// </summary>
    public int? WorkingDaysPerMonth { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// The days an employee (an engineer, an office employee) worked on one project in one month, as recorded by HR from
/// the site attendance. Approved by someone else (hr Approve), then charged to the project by the allocation run.
/// An approved entry is never edited: a correction is a new entry that supersedes it when approved.
/// </summary>
internal sealed class ProjectWorkDays
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public int ProjectId { get; set; }

    /// <summary>The project item the days were spent on, if any (same project, asked from Projects).</summary>
    public int? ProjectItemId { get; set; }

    /// <summary>The first day of the month.</summary>
    public DateOnly Month { get; set; }

    /// <summary>Whole or half days.</summary>
    public decimal Days { get; set; }

    public string? Notes { get; set; }

    private ProjectDaysStatus _status = ProjectDaysStatus.Recorded;

    /// <summary>Set when the record is created; afterwards only this class's methods change it.</summary>
    public ProjectDaysStatus Status { get => _status; init => _status = value; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The recorder. They can fix a returned entry; they can never review it.</summary>
    public int CreatedBy { get; set; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<ProjectDaysStatus> Flow = new(
        (ProjectDaysStatus.Recorded, ProjectDaysStatus.Approved),
        (ProjectDaysStatus.Recorded, ProjectDaysStatus.Returned),
        (ProjectDaysStatus.Recorded, ProjectDaysStatus.Rejected),
        (ProjectDaysStatus.Recorded, ProjectDaysStatus.Recorded),
        (ProjectDaysStatus.Returned, ProjectDaysStatus.Recorded),
        (ProjectDaysStatus.Approved, ProjectDaysStatus.Superseded));

    public void Review(ProjectDaysStatus outcome, int reviewerId, DateTime now, string? note)
    {
        if (outcome is not (ProjectDaysStatus.Approved or ProjectDaysStatus.Returned or ProjectDaysStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A review approves, returns or rejects.");
        }

        _status = Flow.Move(_status, outcome);
        ReviewedAt = now;
        ReviewedBy = reviewerId;
        ReviewNote = note;
    }

    /// <summary>Fixed by its recorder and sent again: Recorded, with the last review cleared.</summary>
    public void Resubmit(int userId, DateTime now)
    {
        _status = Flow.Move(_status, ProjectDaysStatus.Recorded);
        ReviewedAt = null;
        ReviewedBy = null;
        ReviewNote = null;
        UpdatedAt = now;
        UpdatedBy = userId;
    }

    /// <summary>A newer approved entry for the same employee, project and month replaces this one.</summary>
    public void Supersede(int userId, DateTime now)
    {
        _status = Flow.Move(_status, ProjectDaysStatus.Superseded);
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}

/// <summary>Stored by name.</summary>
internal enum ProjectDaysStatus
{
    Recorded,
    Approved,
    Returned,
    Rejected,

    /// <summary>Replaced by a newer approved entry (a correction). Its allocation is reversed by the next run.</summary>
    Superseded,
}

/// <summary>
/// The part of a monthly salary charged to a project for approved days: (basic + fixed allowance) ÷ working days ×
/// days. Written by the allocation run, never edited; when the days or the salary change, the run reverses it (kept,
/// with who and why) and writes a new one. The salary payment itself stays company money in the ledger, so the cost
/// is charged once.
/// </summary>
internal sealed class SalaryCostAllocation
{
    public int Id { get; set; }

    public int WorkDaysId { get; set; }

    public int EmployeeId { get; set; }

    public int ProjectId { get; set; }

    public int? ProjectItemId { get; set; }

    public DateOnly Month { get; set; }

    public decimal Days { get; set; }

    /// <summary>Basic + fixed allowance of the salary version used.</summary>
    public decimal MonthlyPay { get; set; }

    public int WorkingDays { get; set; }

    public decimal Amount { get; set; }

    public AllocationStatus Status { get; set; } = AllocationStatus.Active;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public DateTime? ReversedAt { get; set; }
    public int? ReversedBy { get; set; }
    public string? ReverseReason { get; set; }
}

/// <summary>Stored by name.</summary>
internal enum AllocationStatus
{
    Active,
    Reversed,
}
