namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>
/// Money paid to one worker. It settles the worker's oldest approved, unpaid days first, across every project, and
/// the allocations say exactly which days, so one payment is never counted twice. Never edited or deleted: a wrong
/// payment is voided, and a voided payment and its allocations count nowhere.
/// </summary>
/// <remarks>
/// Until the Finance module exists, this table stands in for its payment spine (decision G3). A payment can't be
/// larger than what the worker is still owed: there are no worker advances in this version (decision W2).
/// </remarks>
internal sealed class WorkerPayment
{
    public int Id { get; set; }

    public int WorkerId { get; set; }
    public Worker Worker { get; set; } = null!;

    public DateOnly PaymentDate { get; set; }

    /// <summary>Equal to the sum of the allocations.</summary>
    public decimal Amount { get; set; }

    public WorkerPaymentMethod Method { get; set; }

    public string? Reference { get; set; }

    public string? Notes { get; set; }

    /// <summary>Made by the client for each payment form. The same key twice returns the first payment.</summary>
    public Guid IdempotencyKey { get; set; }

    public DateTime? VoidedAt { get; set; }
    public int? VoidedBy { get; set; }
    public string? VoidReason { get; set; }

    /// <summary>
    /// The weekly settlement that paid it in cash out of a custody, or <c>null</c> for a payment made through a
    /// Finance payment request. Such a payment is voided only by reversing its settlement.
    /// </summary>
    public int? SettlementId { get; set; }
    public PayrollSettlement? Settlement { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<WorkerPaymentAllocation> Allocations { get; } = [];

    public bool IsVoided => VoidedAt is not null;
}

/// <summary>Stored by name.</summary>
internal enum WorkerPaymentMethod
{
    Cash,
    BankTransfer,
}

/// <summary>The part of one payment that settles one approved labor line.</summary>
internal sealed class WorkerPaymentAllocation
{
    public int Id { get; set; }

    public int PaymentId { get; set; }
    public WorkerPayment Payment { get; set; } = null!;

    public int LineId { get; set; }
    public AttendanceLine Line { get; set; } = null!;

    public decimal Amount { get; set; }
}
