using AlMotaheda.BuildingBlocks.Domain;
namespace AlMotaheda.Modules.Fleet.Data;

/// <summary>
/// One cost of a machine on one project: rental, fuel, maintenance, repair, transport or other. This row is the
/// source record of the project's equipment cost; nothing copies it. Fuel, rental, maintenance and repair of
/// equipment are recorded only here, never as project expenses, so they are counted once.
/// </summary>
/// <remarks>
/// <para><b>Project:</b> the cost belongs to <see cref="ProjectId"/>, not to wherever the machine is today. The
/// machine must have been on that project at some time.</para>
/// <para><b>Amount:</b> set by the server. Fuel = litres × price per litre; a day, month or hour basis = quantity ×
/// rate; a lump sum = the typed amount. The rate is copied here, so later rental terms never change it.</para>
/// <para><b>Recognition:</b> a cost of the project only while <see cref="Status"/> is
/// <see cref="EquipmentCostStatus.Approved"/>. Approved rows are frozen.</para>
/// <para><b>Payment:</b> <see cref="CostChannel.Custody"/> is paid once approved. <see cref="CostChannel.Credit"/>
/// is paid when its one full payment is recorded (<see cref="PaidOn"/>; decision F2).</para>
/// </remarks>
internal sealed class EquipmentCost
{
    public int Id { get; set; }

    public int EquipmentId { get; set; }
    public Equipment Equipment { get; set; } = null!;

    /// <summary>The project (<c>projects.Project</c>). A plain id: the Projects module owns the table.</summary>
    public int ProjectId { get; set; }

    /// <summary>The business date of the cost.</summary>
    public DateOnly CostDate { get; set; }

    public EquipmentCostType Type { get; set; }

    /// <summary>Every type except fuel. <see cref="CostBasis.Lump"/> means the amount was typed.</summary>
    public CostBasis? Basis { get; set; }

    /// <summary>Days, months or hours, for a basis other than lump.</summary>
    public decimal? Quantity { get; set; }

    /// <summary>The rate per day, month or hour, copied on the cost.</summary>
    public decimal? Rate { get; set; }

    /// <summary>Fuel only.</summary>
    public decimal? Liters { get; set; }

    /// <summary>Fuel only.</summary>
    public decimal? PricePerLiter { get; set; }

    public decimal Amount { get; set; }

    public string Description { get; set; } = "";

    /// <summary>A supplier of the Projects module (<c>projects.Supplier</c>). Required for rental and for credit.</summary>
    public int? SupplierId { get; set; }

    public CostChannel Channel { get; set; }

    public string? InvoiceNo { get; set; }

    /// <summary>
    /// The project item ("بند") this cost belongs to, or empty while it is "not linked to an item yet" (docs/cost-flow
    /// §2.4, replacing decision F1). A link, not a copy: the cost stays one record, counted once.
    /// </summary>
    public int? ProjectItemId { get; set; }

    public DateTime? ItemLinkedAt { get; set; }
    public int? ItemLinkedBy { get; set; }

    private EquipmentCostStatus _status = EquipmentCostStatus.Recorded;

    /// <summary>Set when the record is created; afterwards only this class's methods change it, through <see cref="Flow"/>.</summary>
    public EquipmentCostStatus Status { get => _status; init => _status = value; }

    public DateTime? ReviewedAt { get; set; }
    public int? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }

    /// <summary>Credit only: the date of the one payment for the full amount. Empty while unpaid.</summary>
    public DateOnly? PaidOn { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }
    public string? PaymentReference { get; set; }
    public DateTime? PaymentRecordedAt { get; set; }
    public int? PaymentRecordedBy { get; set; }

    /// <summary>
    /// The last payment that was removed as wrong: who, when and why. The payment columns above are emptied by the
    /// void, so the cost can be paid again; every voided payment, with all its details, is also kept as a
    /// <see cref="VoidedEquipmentCostPayment"/> row, so a second pay and void never overwrites the first one.
    /// </summary>
    public DateTime? PaymentVoidedAt { get; set; }
    public int? PaymentVoidedBy { get; set; }
    public string? PaymentVoidReason { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The recorder. They can fix a returned row; they can never review it.</summary>
    public int CreatedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    /// <summary>Corrections after approval (refactor plan M6). Approved ones change every figure of the cost.</summary>
    public List<EquipmentCostAdjustment> Adjustments { get; } = [];

    /// <summary>Recorded → Approved, Returned or Rejected by a reviewer; Recorded or Returned → Recorded when its recorder fixes it.</summary>
    private static readonly StatusFlow<EquipmentCostStatus> Flow = new(
        (EquipmentCostStatus.Recorded, EquipmentCostStatus.Approved),
        (EquipmentCostStatus.Recorded, EquipmentCostStatus.Returned),
        (EquipmentCostStatus.Recorded, EquipmentCostStatus.Rejected),
        (EquipmentCostStatus.Recorded, EquipmentCostStatus.Recorded),
        (EquipmentCostStatus.Returned, EquipmentCostStatus.Recorded));

    public void Review(EquipmentCostStatus outcome, int reviewerId, DateTime now, string? note)
    {
        if (outcome == EquipmentCostStatus.Recorded)
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
        _status = Flow.Move(_status, EquipmentCostStatus.Recorded);
        ReviewedAt = null;
        ReviewedBy = null;
        ReviewNote = null;
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}

/// <summary>
/// A payment of a credit equipment cost that was voided as wrong, with who voided it, when and why. Written once,
/// at the void, and never edited or deleted: the history of payments of a cost (payments are never edited or
/// deleted, overview section 3.4). A voided payment is never paid money.
/// </summary>
internal sealed class VoidedEquipmentCostPayment
{
    public int Id { get; set; }

    public int CostId { get; set; }
    public EquipmentCost Cost { get; set; } = null!;

    public DateOnly PaidOn { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public string? PaymentReference { get; set; }
    public DateTime PaymentRecordedAt { get; set; }
    public int PaymentRecordedBy { get; set; }

    public DateTime VoidedAt { get; set; }
    public int VoidedBy { get; set; }
    public string VoidReason { get; set; } = "";
}

/// <summary>The kinds of equipment cost. Stored by name.</summary>
internal enum EquipmentCostType
{
    Rental,
    Fuel,
    Maintenance,
    Repair,

    /// <summary>Spare parts and replacement components bought for the machine.</summary>
    SpareParts,
    Transport,
    Other,
}

/// <summary>How the amount of a cost other than fuel is worked out. Stored by name.</summary>
internal enum CostBasis
{
    Day,
    Month,
    Hour,

    /// <summary>One typed amount.</summary>
    Lump,
}

/// <summary>How a cost is paid. Stored by name.</summary>
internal enum CostChannel
{
    /// <summary>Paid on site from a custody fund. Counts as paid once the cost is approved.</summary>
    Custody,

    /// <summary>A supplier invoice the company pays later. Paid only when its payment is recorded.</summary>
    Credit,
}

internal enum EquipmentCostStatus
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

/// <summary>How the money of a credit cost left. Stored by name.</summary>
internal enum PaymentMethod
{
    BankTransfer,
    Cheque,
    Cash,
}
