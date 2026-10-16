using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Procurement.Data;

/// <summary>A goods receipt note (GRN): what arrived on site against one PO, recorded by the receiving person.</summary>
internal sealed class GoodsReceipt
{
    public int Id { get; set; }

    /// <summary><c>GRN-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    public int OrderId { get; set; }

    public int ProjectId { get; set; }

    public DateOnly ReceivedOn { get; set; }

    public string? DeliveryNoteNo { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public List<GoodsReceiptLine> Lines { get; } = [];
}

internal sealed class GoodsReceiptLine
{
    public int Id { get; set; }

    public int ReceiptId { get; set; }

    public int OrderLineId { get; set; }

    public decimal Quantity { get; set; }
}

/// <summary>
/// A supplier invoice against one PO, recorded by the Accountant and checked line by line against the PO and the
/// receipts (3-way match). A matched invoice is posted at once as the project's supplier cost; a mismatch waits for
/// the Accountant's resolution.
/// </summary>
internal sealed class SupplierInvoice
{
    private static readonly StatusFlow<SupplierInvoiceStatus> Flow = new(
        (SupplierInvoiceStatus.Matched, SupplierInvoiceStatus.Posted),
        (SupplierInvoiceStatus.Mismatch, SupplierInvoiceStatus.Posted),
        (SupplierInvoiceStatus.Mismatch, SupplierInvoiceStatus.Returned));

    private SupplierInvoiceStatus _status;

    public int Id { get; set; }

    /// <summary><c>INV-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    public int OrderId { get; set; }

    public int ProjectId { get; set; }

    public int SupplierId { get; set; }

    /// <summary>The supplier's own invoice number. Unique per supplier among invoices not returned.</summary>
    public string InvoiceNo { get; set; } = "";

    public DateOnly InvoiceDate { get; set; }

    public string? DeliveryNoteNo { get; set; }

    public SupplierInvoiceStatus Status { get => _status; init => _status = value; }

    /// <summary>Σ invoiced quantity × invoice unit price.</summary>
    public decimal Total { get; set; }

    public InvoiceResolution? Resolution { get; private set; }
    public string? ResolutionReason { get; private set; }
    public string? CreditNoteNo { get; private set; }
    public DateTime? ResolvedAt { get; private set; }
    public int? ResolvedBy { get; private set; }

    public DateTime? PostedAt { get; private set; }
    public int? PostedBy { get; private set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<SupplierInvoiceLine> Lines { get; } = [];

    /// <summary>A returned invoice no longer counts: its quantities can be invoiced again.</summary>
    public bool Counts => Status != SupplierInvoiceStatus.Returned;

    public void Resolve(InvoiceResolution resolution, string reason, string? creditNoteNo, int userId, DateTime now)
    {
        if (resolution == InvoiceResolution.ReturnToSupplier)
        {
            _status = Flow.Move(Status, SupplierInvoiceStatus.Returned);
        }
        else if (Status != SupplierInvoiceStatus.Mismatch)
        {
            throw new InvalidOperationException("Only a mismatched invoice is resolved.");
        }

        Resolution = resolution;
        ResolutionReason = reason;
        CreditNoteNo = creditNoteNo;
        ResolvedAt = now;
        ResolvedBy = userId;
    }

    public void MarkPosted(int userId, DateTime now)
    {
        _status = Flow.Move(Status, SupplierInvoiceStatus.Posted);
        PostedAt = now;
        PostedBy = userId;
    }
}

/// <summary>Stored by name.</summary>
internal enum SupplierInvoiceStatus
{
    Matched,
    Mismatch,
    Posted,
    Returned,
}

/// <summary>Stored by name. How the Accountant resolved a mismatch.</summary>
internal enum InvoiceResolution
{
    /// <summary>Accept the invoice price, with a reason; posted at the invoice price.</summary>
    Accept,

    /// <summary>The supplier credits the difference; posted at the PO price.</summary>
    CreditNote,

    /// <summary>Sent back; nothing posted, its quantities free again.</summary>
    ReturnToSupplier,
}

/// <summary>One invoiced line: quantity and price, with the PO price it was matched against.</summary>
internal sealed class SupplierInvoiceLine
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }

    public int OrderLineId { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    /// <summary>The PO price at recording.</summary>
    public decimal OrderUnitPrice { get; set; }

    /// <summary>The price is within the tolerance of the PO price.</summary>
    public bool PriceMatches { get; set; }
}
