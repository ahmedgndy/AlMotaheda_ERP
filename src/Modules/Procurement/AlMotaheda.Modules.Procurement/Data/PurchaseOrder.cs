using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Procurement.Data;

/// <summary>
/// A purchase order (PO) to one supplier, from an approved PR. Its prices are the committed prices and are never
/// recalculated. Every change after issue is a numbered revision with a reason and the previous lines kept.
/// </summary>
internal sealed class PurchaseOrder
{
    private static readonly StatusFlow<PurchaseOrderStatus> Flow = new(
        (PurchaseOrderStatus.Issued, PurchaseOrderStatus.PartiallyReceived),
        (PurchaseOrderStatus.Issued, PurchaseOrderStatus.Received),
        (PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received),
        (PurchaseOrderStatus.Received, PurchaseOrderStatus.PartiallyReceived),
        (PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Issued),
        (PurchaseOrderStatus.Issued, PurchaseOrderStatus.Cancelled));

    private PurchaseOrderStatus _status = PurchaseOrderStatus.Issued;

    public int Id { get; set; }

    /// <summary><c>PO-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    public int RequisitionId { get; set; }

    public int ProjectId { get; set; }

    public int SupplierId { get; set; }

    /// <summary>The selected quotation this order came from, when quotations were asked for.</summary>
    public int? QuotationId { get; set; }

    public PurchaseOrderStatus Status { get => _status; init => _status = value; }

    /// <summary>0 as issued; +1 for each revision.</summary>
    public int Revision { get; private set; }

    public DateOnly? ExpectedDelivery { get; set; }

    public string? PaymentTerms { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public DateTime? CancelledAt { get; private set; }
    public int? CancelledBy { get; private set; }
    public string? CancelReason { get; private set; }

    public byte[] RowVersion { get; set; } = [];

    public List<PurchaseOrderLine> Lines { get; } = [];

    public List<PurchaseOrderRevision> Revisions { get; } = [];

    public bool IsOpen => Status is PurchaseOrderStatus.Issued or PurchaseOrderStatus.PartiallyReceived;

    /// <summary>Keeps the lines as they were, then counts the revision. The caller changes the lines after.</summary>
    public void Revise(string previousLines, string reason, int userId, DateTime now)
    {
        Revision++;
        Revisions.Add(new PurchaseOrderRevision
        {
            RevisionNo = Revision,
            Reason = reason,
            PreviousLines = previousLines,
            CreatedAt = now,
            CreatedBy = userId,
        });
        UpdatedAt = now;
        UpdatedBy = userId;
    }

    /// <summary>The receipt status from what was received of every line.</summary>
    public void Received(bool anything, bool everything)
    {
        var target = everything ? PurchaseOrderStatus.Received : anything ? PurchaseOrderStatus.PartiallyReceived : PurchaseOrderStatus.Issued;
        if (Status != target)
        {
            _status = Flow.Move(Status, target);
        }
    }

    public void Cancel(int userId, DateTime now, string reason)
    {
        _status = Flow.Move(Status, PurchaseOrderStatus.Cancelled);
        CancelledAt = now;
        CancelledBy = userId;
        CancelReason = reason;
    }
}

/// <summary>Stored by name.</summary>
internal enum PurchaseOrderStatus
{
    Issued,
    PartiallyReceived,
    Received,
    Cancelled,
}

/// <summary>One ordered line, tied to its PR line (and through it to the catalog item and cost item).</summary>
internal sealed class PurchaseOrderLine
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public int RequisitionLineId { get; set; }

    public int? CatalogItemId { get; set; }

    public int? ProjectItemId { get; set; }

    public string Description { get; set; } = "";

    public string Unit { get; set; } = "";

    public decimal Quantity { get; set; }

    /// <summary>The committed price.</summary>
    public decimal UnitPrice { get; set; }
}

/// <summary>A revision: why, by whom, and the lines as they were before it (JSON).</summary>
internal sealed class PurchaseOrderRevision
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public int RevisionNo { get; set; }

    public string Reason { get; set; } = "";

    public string PreviousLines { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
}
