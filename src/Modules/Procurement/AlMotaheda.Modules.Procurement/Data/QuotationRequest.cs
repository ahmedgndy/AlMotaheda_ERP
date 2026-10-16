using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Procurement.Data;

/// <summary>
/// A request for quotations (RFQ) on an approved PR, asked by the Executive. Purchasing adds quotations, may
/// recommend one, and submits the set; the Executive selects one or returns the set with a note.
/// </summary>
internal sealed class QuotationRequest
{
    private static readonly StatusFlow<QuotationRequestStatus> Flow = new(
        (QuotationRequestStatus.Open, QuotationRequestStatus.Submitted),
        (QuotationRequestStatus.Returned, QuotationRequestStatus.Submitted),
        (QuotationRequestStatus.Submitted, QuotationRequestStatus.Selected),
        (QuotationRequestStatus.Submitted, QuotationRequestStatus.Returned));

    private QuotationRequestStatus _status = QuotationRequestStatus.Open;

    public int Id { get; set; }

    /// <summary><c>RFQ-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    public int RequisitionId { get; set; }

    public int ProjectId { get; set; }

    public QuotationRequestStatus Status { get => _status; init => _status = value; }

    /// <summary>How many quotations the Executive wants; <c>null</c> when not said.</summary>
    public int? QuotationsWanted { get; set; }

    public string? RequestNote { get; set; }

    public DateTime RequestedAt { get; set; }
    public int RequestedBy { get; set; }

    public int? RecommendedQuotationId { get; private set; }
    public string? RecommendationReason { get; private set; }

    public DateTime? SubmittedAt { get; private set; }
    public int? SubmittedBy { get; private set; }

    public DateTime? DecidedAt { get; private set; }
    public int? DecidedBy { get; private set; }

    /// <summary>Why this quotation (required when it isn't the cheapest), or why the set was returned.</summary>
    public string? DecisionNote { get; private set; }

    public int? SelectedQuotationId { get; private set; }

    public byte[] RowVersion { get; set; } = [];

    public List<Quotation> Quotations { get; } = [];

    /// <summary>Purchasing may add quotations and recommend one while the set is with it.</summary>
    public bool IsWithPurchasing => Status is QuotationRequestStatus.Open or QuotationRequestStatus.Returned;

    public void Recommend(int? quotationId, string? reason)
    {
        RecommendedQuotationId = quotationId;
        RecommendationReason = reason;
    }

    public void Submit(int userId, DateTime now)
    {
        _status = Flow.Move(Status, QuotationRequestStatus.Submitted);
        SubmittedAt = now;
        SubmittedBy = userId;
    }

    public void Select(int quotationId, int userId, DateTime now, string? reason)
    {
        _status = Flow.Move(Status, QuotationRequestStatus.Selected);
        SelectedQuotationId = quotationId;
        DecidedAt = now;
        DecidedBy = userId;
        DecisionNote = reason;
    }

    public void Return(int userId, DateTime now, string note)
    {
        _status = Flow.Move(Status, QuotationRequestStatus.Returned);
        DecidedAt = now;
        DecidedBy = userId;
        DecisionNote = note;
    }
}

/// <summary>Stored by name.</summary>
internal enum QuotationRequestStatus
{
    Open,
    Submitted,
    Returned,
    Selected,
}

/// <summary>One supplier's offer for the PR's lines.</summary>
internal sealed class Quotation
{
    public int Id { get; set; }

    public int RequestId { get; set; }

    public int SupplierId { get; set; }

    public string? SupplierReference { get; set; }

    /// <summary>Days from order to delivery.</summary>
    public int? DeliveryDays { get; set; }

    public string? PaymentTerms { get; set; }

    public DateOnly? ValidUntil { get; set; }

    public string? Notes { get; set; }

    /// <summary>Σ PR quantity × quoted unit price.</summary>
    public decimal Total { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public List<QuotationLine> Lines { get; } = [];
}

/// <summary>The quoted unit price of one PR line.</summary>
internal sealed class QuotationLine
{
    public int Id { get; set; }

    public int QuotationId { get; set; }

    public int RequisitionLineId { get; set; }

    public decimal UnitPrice { get; set; }
}
