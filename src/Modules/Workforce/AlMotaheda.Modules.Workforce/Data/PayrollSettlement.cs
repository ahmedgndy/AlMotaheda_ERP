using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>
/// A weekly labor settlement ("تسوية الأسبوعية"): wages a custody holder paid in cash on one project for a period,
/// worker by worker, each amount allocated to that worker's approved, unpaid days of the period on this project.
/// </summary>
/// <remarks>
/// <para>The cash leaves the holder's custody when the settlement is recorded (Projects' custody ledger, Pending).
/// The days are held for it from then on, so no other payment or settlement can take them.</para>
/// <para>It becomes paid labor only when a custody reviewer (never the recorder, never the holder) confirms it: one
/// <see cref="WorkerPayment"/> per worker is written with the planned allocations, and the custody outflow is
/// posted. Rejected: the days are free again and the custody gets the cash back. A confirmed settlement can be
/// reversed (its payments are voided, the custody outflow released). Never deleted.</para>
/// </remarks>
internal sealed class PayrollSettlement
{
    public int Id { get; set; }

    /// <summary>The project (<c>projects.Project</c>, a foreign key added by hand in the migration).</summary>
    public int ProjectId { get; set; }

    /// <summary>The first and last work days the wages are for.</summary>
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }

    /// <summary>The day the cash was handed out.</summary>
    public DateOnly PaidOn { get; set; }

    /// <summary>Whose custody the cash came from (an <c>access.User</c>).</summary>
    public int PaidByUserId { get; set; }

    /// <summary>Always Cash: a settlement is cash paid out of a custody.</summary>
    public WorkerPaymentMethod Method { get; set; } = WorkerPaymentMethod.Cash;

    /// <summary>Σ of the lines.</summary>
    public decimal Amount { get; set; }

    public string? Reference { get; set; }
    public string? Notes { get; set; }

    private SettlementStatus _status = SettlementStatus.Pending;

    public SettlementStatus Status { get => _status; init => _status = value; }

    public int RecordedBy { get; set; }
    public DateTime RecordedAt { get; set; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    public DateTime? ReversedAt { get; set; }
    public int? ReversedBy { get; set; }
    public string? ReverseReason { get; set; }

    /// <summary>Made by the client for each settlement form. The same key twice returns the first settlement.</summary>
    public Guid IdempotencyKey { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<PayrollSettlementLine> Lines { get; } = [];

    /// <summary>Pending → Confirmed or Rejected by a reviewer; Confirmed → Reversed.</summary>
    private static readonly StatusFlow<SettlementStatus> Flow = new(
        (SettlementStatus.Pending, SettlementStatus.Confirmed),
        (SettlementStatus.Pending, SettlementStatus.Rejected),
        (SettlementStatus.Confirmed, SettlementStatus.Reversed));

    public void Review(SettlementStatus outcome, int reviewerId, DateTime now, string? note)
    {
        _status = Flow.Move(_status, outcome);
        ReviewedAt = now;
        ReviewedBy = reviewerId;
        ReviewNote = note;
    }

    public void Reverse(int userId, DateTime now, string reason)
    {
        _status = Flow.Move(_status, SettlementStatus.Reversed);
        ReversedAt = now;
        ReversedBy = userId;
        ReverseReason = reason;
    }
}

/// <summary>Stored by name.</summary>
internal enum SettlementStatus
{
    /// <summary>Paid on site and recorded; the days are held; not paid labor yet.</summary>
    Pending,
    Confirmed,
    Rejected,
    Reversed,
}

/// <summary>What one worker was paid in a settlement.</summary>
internal sealed class PayrollSettlementLine
{
    public int Id { get; set; }

    public int SettlementId { get; set; }
    public PayrollSettlement Settlement { get; set; } = null!;

    public int WorkerId { get; set; }
    public Worker Worker { get; set; } = null!;

    /// <summary>Σ of the allocations.</summary>
    public decimal Amount { get; set; }

    /// <summary>The worker payment written when the settlement was confirmed.</summary>
    public int? PaymentId { get; set; }
    public WorkerPayment? Payment { get; set; }

    public List<PayrollSettlementAllocation> Allocations { get; } = [];
}

/// <summary>The part of a settlement line that pays one approved labor line (a worker's day).</summary>
internal sealed class PayrollSettlementAllocation
{
    public int Id { get; set; }

    public int SettlementLineId { get; set; }
    public PayrollSettlementLine SettlementLine { get; set; } = null!;

    public int LineId { get; set; }
    public AttendanceLine Line { get; set; } = null!;

    public decimal Amount { get; set; }
}

/// <summary>
/// One alert to one person that a settlement was recorded (the project's managers, the Executives, the accountants).
/// Saved with the settlement; the Platform outbox turns it into a notification after the commit.
/// </summary>
internal sealed class SettlementAlert
{
    public int Id { get; set; }

    public int SettlementId { get; set; }
    public PayrollSettlement Settlement { get; set; } = null!;

    public int ProjectId { get; set; }

    public int RecipientUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
