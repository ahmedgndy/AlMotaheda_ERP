namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// Money paid by the company to a supplier for one project, settling approved <see cref="ProjectExpense"/> rows
/// bought on credit. The payment is a fact: it is never edited or deleted. A wrong payment is voided, and a voided
/// payment and its allocations are ignored by every calculation.
/// </summary>
/// <remarks>
/// <para>The amount always equals the sum of its <see cref="Allocations"/>; the server checks this before saving.
/// What a supplier is still owed is never stored: it is the approved credit amounts minus the allocations of
/// payments that are not voided.</para>
/// <para>Until the Finance module exists, this table stands in for its payment spine (decision G3). Its rows keep
/// the same invariants, so they can be moved under Finance later.</para>
/// </remarks>
internal sealed class SupplierPayment
{
    public int Id { get; set; }

    /// <summary>Every allocation of this payment is on a record of this project.</summary>
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    public DateOnly PaymentDate { get; set; }

    /// <summary>Equal to the sum of the allocations.</summary>
    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    /// <summary>Bank or cheque reference.</summary>
    public string? Reference { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Made by the client for each payment form. Sending the same key twice returns the first payment and records
    /// nothing, so a retried request can't pay a supplier twice.
    /// </summary>
    public Guid IdempotencyKey { get; set; }

    /// <summary>Set together with <see cref="VoidedBy"/> and <see cref="VoidReason"/>. A voided payment pays nothing.</summary>
    public DateTime? VoidedAt { get; set; }
    public int? VoidedBy { get; set; }
    public string? VoidReason { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<SupplierPaymentAllocation> Allocations { get; } = [];

    public bool IsVoided => VoidedAt is not null;
}

/// <summary>How the money left. Stored by name.</summary>
internal enum PaymentMethod
{
    BankTransfer,
    Cheque,
    Cash,
}

/// <summary>
/// The part of one <see cref="SupplierPayment"/> that settles one <see cref="ProjectExpense"/>. One row per
/// payment and expense. The sum of the allocations on an expense (payments not voided) can never exceed the
/// expense amount; the server checks this under the project lock.
/// </summary>
internal sealed class SupplierPaymentAllocation
{
    public int Id { get; set; }

    public int PaymentId { get; set; }
    public SupplierPayment Payment { get; set; } = null!;

    public int ExpenseId { get; set; }
    public ProjectExpense Expense { get; set; } = null!;

    public decimal Amount { get; set; }
}
