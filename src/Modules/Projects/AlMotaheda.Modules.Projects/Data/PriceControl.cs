using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// A category of the price list ("ضبط الأسعار"): construction materials, worker meals, fuel, internet, office
/// expenses, maintenance, transport, equipment rental… Configurable: the company adds its own; the first ones are
/// seeded from the project catalog's classifications so existing price items keep their category.
/// </summary>
internal sealed class PriceCategory
{
    public int Id { get; set; }

    /// <summary>Unique, stable, Latin letters and digits: the client keys its labels on it.</summary>
    public required string Code { get; set; }

    public required string NameAr { get; set; }
    public required string NameEn { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// A unit prices are quoted per: ton, bag, kilogram, cubic meter, liter, item, worker/day, meal, monthly
/// subscription… Configurable; seeded with the shared measurement units every record already uses.
/// </summary>
internal sealed class PriceUnit
{
    public int Id { get; set; }

    /// <summary>Unique, stable: price items and records store the code.</summary>
    public required string Code { get; set; }

    public required string NameAr { get; set; }
    public required string NameEn { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>How a range is expressed. Stored by name on <see cref="PriceRange"/>.</summary>
internal enum PriceRuleKind
{
    /// <summary>A lowest and a highest accepted unit price.</summary>
    Range,

    /// <summary>One reference price, with an optional tolerance in percent around it.</summary>
    Fixed,

    /// <summary>A whole service or a recurring expense (a subscription, a monthly fee): the record's amount is compared, quantity 1.</summary>
    LumpSum,
}

/// <summary>
/// One transaction whose price was outside its reference range: a case for the Executive. The transaction itself
/// (a project record, a supplier invoice line) stays what it is: a true record of what happened, waiting. The
/// Executive approves it once, approves it and changes the catalog, or rejects it.
/// </summary>
/// <remarks>
/// Under <c>Projects:PriceCheckPolicy = ExecutiveApproval</c> the case gates the transaction: the record can't be
/// approved and nothing can be paid on it until the case is approved (<c>PriceGate</c>). Under <c>Warn</c> the case is
/// opened for the inbox and the reports and the existing review decides the record. Insert-only, except the
/// decision; superseded cases are marked <see cref="PriceExceptionStatus.Withdrawn"/>, never deleted.
/// </remarks>
internal sealed class PriceException
{
    public int Id { get; set; }

    /// <summary>One of <c>PriceExceptionSources</c>: the module record that carries the price.</summary>
    public required string SourceType { get; set; }

    public int SourceId { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public int? SupplierId { get; set; }

    public int PriceItemId { get; set; }
    public PriceItem PriceItem { get; set; } = null!;

    /// <summary>The range the price was compared with; empty when none applied (no reference on the date).</summary>
    public int? PriceRangeId { get; set; }

    public required string Unit { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>The entered price per unit (the amount, for a lump sum).</summary>
    public decimal UnitPrice { get; set; }

    public decimal? ReferenceMinPrice { get; set; }
    public decimal? ReferenceMaxPrice { get; set; }

    /// <summary>Entered − highest accepted, per unit (positive).</summary>
    public decimal VariancePerUnit { get; set; }

    /// <summary>round(variance per unit × quantity, 2).</summary>
    public decimal VarianceTotal { get; set; }

    /// <summary>Variance per unit as a percentage of the highest accepted price, rounded to 2 decimals.</summary>
    public decimal VariancePercent { get; set; }

    /// <summary>Why the submitter paid or agreed this price.</summary>
    public string? Reason { get; set; }

    /// <summary>A short description of the transaction for the inbox (item name, invoice number).</summary>
    public required string Summary { get; set; }

    public DateOnly TransactionDate { get; set; }

    public int SubmittedBy { get; set; }
    public DateTime SubmittedAt { get; set; }

    private PriceExceptionStatus _status = PriceExceptionStatus.Pending;

    public PriceExceptionStatus Status { get => _status; init => _status = value; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    /// <summary>The approved range the Executive created with decision B, so the catalog change is traceable to the case.</summary>
    public int? NewRangeId { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<PriceExceptionStatus> Flow = new(
        (PriceExceptionStatus.Pending, PriceExceptionStatus.ApprovedOnce),
        (PriceExceptionStatus.Pending, PriceExceptionStatus.ApprovedWithPriceChange),
        (PriceExceptionStatus.Pending, PriceExceptionStatus.Rejected),
        (PriceExceptionStatus.Pending, PriceExceptionStatus.Withdrawn),
        (PriceExceptionStatus.Rejected, PriceExceptionStatus.Withdrawn));

    public void Decide(PriceExceptionStatus outcome, int deciderId, DateTime now, string note, int? newRangeId = null)
    {
        _status = Flow.Move(_status, outcome);
        DecidedAt = now;
        DecidedBy = deciderId;
        DecisionNote = note;
        NewRangeId = newRangeId;
    }

    /// <summary>The transaction was changed or removed by its submitter: the case no longer applies.</summary>
    public void Withdraw(int userId, DateTime now)
    {
        _status = Flow.Move(_status, PriceExceptionStatus.Withdrawn);
        DecidedAt = now;
        DecidedBy = userId;
    }

    /// <summary>Approved either way: the transaction may go on (be approved, be paid).</summary>
    public bool IsApproved => _status is PriceExceptionStatus.ApprovedOnce or PriceExceptionStatus.ApprovedWithPriceChange;

    /// <summary>Still counts against the transaction: pending or rejected.</summary>
    public bool IsOpen => _status is PriceExceptionStatus.Pending or PriceExceptionStatus.Rejected;
}

internal enum PriceExceptionStatus
{
    Pending,

    /// <summary>Decision A: this transaction only; the catalog is unchanged.</summary>
    ApprovedOnce,

    /// <summary>Decision B: this transaction, and a new reference range for the whole company from its effective date.</summary>
    ApprovedWithPriceChange,

    /// <summary>Decision C: the transaction stays blocked; the submitter corrects it or the accountant returns it.</summary>
    Rejected,

    /// <summary>Superseded: the transaction was changed (a new case) or no longer names the price item.</summary>
    Withdrawn,
}

/// <summary>
/// One alert to one person that a price exception was opened (the project's manager, the Executives and the
/// accountants). An audited row: the Platform outbox turns its insert into a stored notification after the commit.
/// </summary>
internal sealed class PriceExceptionAlert
{
    public int Id { get; set; }

    public int ExceptionId { get; set; }
    public PriceException Exception { get; set; } = null!;

    public int ProjectId { get; set; }

    public int RecipientUserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
