namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// One supplier delivery of materials (<see cref="ExpenseKind.Supply"/>) or one site expense
/// (<see cref="ExpenseKind.Expense"/>) of a project. This row is the source record: the cost, the supplier
/// balance and the project item figures are all calculated from it. Nothing copies it.
/// </summary>
/// <remarks>
/// <para><b>Recognition:</b> the amount is a cost of the project only while <see cref="Status"/> is
/// <see cref="ExpenseStatus.Approved"/>. A recorded, returned or rejected row is not a cost.</para>
/// <para><b>Payment is separate:</b> approved is not paid. A <see cref="PaymentChannel.Custody"/> row was paid on
/// site, so it counts as paid once approved. A <see cref="PaymentChannel.Credit"/> row is paid only through
/// supplier payment allocations.</para>
/// <para><b>Frozen after approval:</b> only the link to a project item (<see cref="ProjectItemId"/>) can change.</para>
/// </remarks>
internal sealed class ProjectExpense
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public ExpenseKind Kind { get; set; }

    /// <summary>The business date of the delivery or the expense.</summary>
    public DateOnly ExpenseDate { get; set; }

    /// <summary>Required for a supply and for anything bought on credit.</summary>
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>Supply only: what was delivered, as typed.</summary>
    public string? ItemName { get; set; }

    /// <summary>Supply only: one of <c>MeasurementUnits</c>.</summary>
    public string? Unit { get; set; }

    /// <summary>Supply only.</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Supply only: the price paid per unit, copied here. A later catalog price change never changes it.</summary>
    public decimal? UnitPrice { get; set; }

    /// <summary>Expense only.</summary>
    public ExpenseCategory? Category { get; set; }

    /// <summary>Required for an expense; optional detail for a supply.</summary>
    public string? Description { get; set; }

    /// <summary>For a supply: quantity × unit price, rounded to two decimals, set by the server.</summary>
    public decimal Amount { get; set; }

    public PaymentChannel Channel { get; set; }

    public string? InvoiceNo { get; set; }
    public string? DeliveryNoteNo { get; set; }

    /// <summary>Optional: the catalog item this supply is a purchase of. Same project.</summary>
    public int? CatalogItemId { get; set; }
    public CatalogItem? CatalogItem { get; set; }

    /// <summary>The approved catalog price on <see cref="ExpenseDate"/>, copied when the row was recorded.</summary>
    public decimal? CatalogPriceAtRecording { get; set; }

    /// <summary>
    /// The project item this cost belongs to; empty while it is "not linked to an item yet". A link, not a copy:
    /// changing it moves the amount between items and never creates a row.
    /// </summary>
    public int? ProjectItemId { get; set; }
    public ProjectItem? ProjectItem { get; set; }

    public DateTime? ItemLinkedAt { get; set; }
    public int? ItemLinkedBy { get; set; }

    public ExpenseStatus Status { get; set; } = ExpenseStatus.Recorded;

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The recorder. They can fix a returned row; they can never review it.</summary>
    public int CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Stored by name.</summary>
internal enum ExpenseKind
{
    /// <summary>Materials delivered by a supplier, with quantity and unit price.</summary>
    Supply,

    /// <summary>A site expense: one amount, with a category.</summary>
    Expense,
}

/// <summary>Site expense categories, from the prototype. Stored by name.</summary>
internal enum ExpenseCategory
{
    FeesTransport,
    Lab,
    Survey,
    Consumables,
    PieceWork,
    Guards,
    Accommodation,
    PettyCash,
    Permits,
    Other,
}

/// <summary>How a cost record is paid. Stored by name.</summary>
internal enum PaymentChannel
{
    /// <summary>Paid on site, in cash, from a custody fund. Counts as paid once the record is approved.</summary>
    Custody,

    /// <summary>A supplier invoice to be paid later by the company. Paid only through payment allocations.</summary>
    Credit,
}

internal enum ExpenseStatus
{
    /// <summary>Waiting for review. Not a cost yet.</summary>
    Recorded,

    /// <summary>Recognized cost. Money fields are frozen.</summary>
    Approved,

    /// <summary>Sent back to the recorder to fix and resubmit. Not a cost.</summary>
    Returned,

    /// <summary>Final. Never a cost.</summary>
    Rejected,
}
