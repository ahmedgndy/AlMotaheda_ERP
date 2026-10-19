using AlMotaheda.BuildingBlocks.Domain;
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

    /// <summary>
    /// Set when another module created this record: <c>SupplierInvoiceLine</c> for a posted Procurement invoice line.
    /// Such a record is approved when created and is never edited as an ordinary expense; the pair is unique, so the
    /// same invoice line is never counted twice.
    /// </summary>
    public string? SourceType { get; set; }
    public int? SourceId { get; set; }

    /// <summary>Optional: the catalog item this supply is a purchase of. Same project.</summary>
    public int? CatalogItemId { get; set; }
    public CatalogItem? CatalogItem { get; set; }

    /// <summary>The approved catalog price on <see cref="ExpenseDate"/>, copied when the row was recorded.</summary>
    public decimal? CatalogPriceAtRecording { get; set; }

    /// <summary>
    /// Supply only, optional: the company price list item (price management) this purchase is compared with. Same
    /// unit as the supply.
    /// </summary>
    public int? PriceItemId { get; set; }
    public PriceItem? PriceItem { get; set; }

    /// <summary>The approved range the unit price was compared with, when one applied on the purchase date.</summary>
    public int? PriceRangeId { get; set; }
    public PriceRange? PriceRange { get; set; }

    /// <summary>The range's minimum and maximum, copied when the record was recorded: a later range never changes them.</summary>
    public decimal? ReferenceMinPrice { get; set; }
    public decimal? ReferenceMaxPrice { get; set; }

    /// <summary>The result of the comparison, computed by the server when the record was recorded or resubmitted.</summary>
    public PriceCheckResult? PriceCheck { get; set; }

    /// <summary>Why the price is above the range: required from the recorder in that case.</summary>
    public string? PriceJustification { get; set; }

    /// <summary>
    /// A client-made key for one "save" of the form. Sending the same key again (a retry, a double tap) returns the
    /// first record and records nothing (unique index), so the same purchase is never charged twice.
    /// </summary>
    public Guid? IdempotencyKey { get; set; }

    /// <summary>
    /// The project item this cost belongs to; empty while it is "not linked to an item yet". A link, not a copy:
    /// changing it moves the amount between items and never creates a row.
    /// </summary>
    public int? ProjectItemId { get; set; }
    public ProjectItem? ProjectItem { get; set; }

    public DateTime? ItemLinkedAt { get; set; }
    public int? ItemLinkedBy { get; set; }

    private ExpenseStatus _status = ExpenseStatus.Recorded;

    /// <summary>Set when the record is created; afterwards only this class's methods change it, through <see cref="Flow"/>.</summary>
    public ExpenseStatus Status { get => _status; init => _status = value; }

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

    /// <summary>Corrections after approval (refactor plan M6). Approved ones change every figure of the record.</summary>
    public List<ExpenseAdjustment> Adjustments { get; } = [];

    /// <summary>Recorded → Approved, Returned or Rejected by a reviewer; Recorded or Returned → Recorded when its recorder fixes it.</summary>
    private static readonly StatusFlow<ExpenseStatus> Flow = new(
        (ExpenseStatus.Recorded, ExpenseStatus.Approved),
        (ExpenseStatus.Recorded, ExpenseStatus.Returned),
        (ExpenseStatus.Recorded, ExpenseStatus.Rejected),
        (ExpenseStatus.Recorded, ExpenseStatus.Recorded),
        (ExpenseStatus.Returned, ExpenseStatus.Recorded));

    /// <summary>Approve, return or reject (the slice checked <c>ExpenseRules.Review</c> first).</summary>
    public void Review(ExpenseStatus outcome, int reviewerId, DateTime now, string? note)
    {
        if (outcome == ExpenseStatus.Recorded)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A review approves, returns or rejects.");
        }

        _status = Flow.Move(_status, outcome);
        ReviewedAt = now;
        ReviewedBy = reviewerId;
        ReviewNote = note;
        UpdatedAt = now;
        UpdatedBy = reviewerId;
    }

    /// <summary>Fixed by its recorder and sent again: Recorded, with the last review cleared.</summary>
    public void Resubmit(int userId, DateTime now)
    {
        _status = Flow.Move(_status, ExpenseStatus.Recorded);
        ReviewedAt = null;
        ReviewedBy = null;
        ReviewNote = null;
        UpdatedAt = now;
        UpdatedBy = userId;
    }
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
