using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// One item or service of the company's price list ("إدارة الأسعار"), such as cement per ton or a labour day. It is
/// company-wide: every project's purchases are compared with the same reference prices. The item holds no price; its
/// prices are <see cref="PriceRange"/> rows.
/// </summary>
/// <remarks>
/// Replaces the project's own "items and prices" list (<see cref="CatalogItem"/>) as the authoritative reference
/// price. The project catalog stays for what already uses it (Procurement estimates, old supplies), unchanged.
/// </remarks>
internal sealed class PriceItem
{
    public int Id { get; set; }

    /// <summary>Unique in the company.</summary>
    public required string NameAr { get; set; }

    /// <summary>Unique in the company.</summary>
    public required string NameEn { get; set; }

    /// <summary>The configurable category (<see cref="PriceCategory"/>).</summary>
    public int CategoryId { get; set; }
    public PriceCategory Category { get; set; } = null!;

    /// <summary>
    /// A <see cref="PriceUnit"/> code. Fixed once the item exists: its ranges and every comparison made with them are
    /// per this unit, so changing it would change the meaning of the history.
    /// </summary>
    public required string Unit { get; set; }

    public string? Notes { get; set; }

    /// <summary>An inactive item keeps its history but is not offered for new purchases.</summary>
    public PriceItemStatus Status { get; set; } = PriceItemStatus.Active;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<PriceRange> Ranges { get; } = [];
}

internal enum PriceItemStatus
{
    Active,
    Inactive,
}

/// <summary>
/// One reference price range of a <see cref="PriceItem"/> from a date on: the lowest and highest unit price the
/// company accepts, in EGP, optionally for one supplier only. Insert-only: a new price is a new row and old rows are
/// the change history. A row changes once, when its request is approved or rejected.
/// </summary>
/// <remarks>
/// The range that applies to a purchase on a date (<c>PriceRanges.ApplicableAsync</c>): the supplier's own approved
/// range with the latest start on or before the date, otherwise the general one; none when that range has expired.
/// A purchase copies the range it was compared with, so a later range never changes an old comparison.
/// </remarks>
internal sealed class PriceRange
{
    public int Id { get; set; }

    public int PriceItemId { get; set; }
    public PriceItem PriceItem { get; set; } = null!;

    /// <summary>Set when the range applies to one supplier only (a negotiated price); empty for the general range.</summary>
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>Per <see cref="PriceItem.Unit"/>.</summary>
    public decimal MinPrice { get; set; }

    /// <summary>At least <see cref="MinPrice"/>. A single reference price is a range with min = max.</summary>
    public decimal MaxPrice { get; set; }

    /// <summary>Always <c>EGP</c> today (overview 3.7). Stored so a comparison can say which currency it used.</summary>
    public string Currency { get; set; } = PriceCurrencies.Egp;

    /// <summary>How the range was expressed; <see cref="MinPrice"/> and <see cref="MaxPrice"/> are what is compared whatever the kind.</summary>
    public PriceRuleKind Kind { get; set; } = PriceRuleKind.Range;

    /// <summary>The company's reference price per unit, between the bounds; the single price of a <see cref="PriceRuleKind.Fixed"/> rule.</summary>
    public decimal? ReferencePrice { get; set; }

    /// <summary>For a fixed rule: how far (percent) above and below the reference price is still accepted.</summary>
    public decimal? TolerancePercent { get; set; }

    /// <summary>Where the price comes from: a quotation, a market survey, a contract (text or link).</summary>
    public string? SourceNote { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    /// <summary>The last day it applies; empty for open-ended.</summary>
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>Supplier or delivery conditions the price assumes (delivered to site, minimum quantity...).</summary>
    public string? Conditions { get; set; }

    private PriceRangeStatus _status = PriceRangeStatus.Pending;

    /// <summary>Set when the row is created; afterwards only <see cref="Decide"/> changes it.</summary>
    public PriceRangeStatus Status { get => _status; init => _status = value; }

    public string? Reason { get; set; }

    public DateTime RequestedAt { get; set; }
    public int RequestedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    private static readonly StatusFlow<PriceRangeStatus> Flow = new(
        (PriceRangeStatus.Pending, PriceRangeStatus.Approved),
        (PriceRangeStatus.Pending, PriceRangeStatus.Rejected));

    public void Decide(PriceRangeStatus outcome, int deciderId, DateTime now, string? note)
    {
        _status = Flow.Move(_status, outcome);
        DecidedAt = now;
        DecidedBy = deciderId;
        DecisionNote = note;
    }
}

internal enum PriceRangeStatus
{
    /// <summary>Waiting for an Executive. Never used for a comparison.</summary>
    Pending,
    Approved,
    Rejected,
}

internal static class PriceCurrencies
{
    public const string Egp = "EGP";
}

/// <summary>How a purchase's unit price compares with the reference range. Stored by name on the purchase.</summary>
internal enum PriceCheckResult
{
    /// <summary>A price item was named, but no approved range applies on the purchase date.</summary>
    NoReference,
    WithinRange,

    /// <summary>Above the maximum: a warning, with the policy's consequence (see <c>PriceCheckPolicy</c>).</summary>
    AboveRange,

    /// <summary>Below the minimum: a warning only (quality or a quantity error), never blocked.</summary>
    BelowRange,
}

/// <summary>
/// What happens when a purchase is above the reference range. Configured in <c>Projects:PriceCheckPolicy</c>.
/// Every policy keeps the existing control that someone other than the recorder approves the purchase.
/// </summary>
internal enum PriceCheckPolicy
{
    /// <summary>Default: the purchase is saved with the warning and a required justification; any reviewer may approve it.</summary>
    Warn,

    /// <summary>As <see cref="Warn"/>, and only an Executive may approve it.</summary>
    ExecutiveApproval,

    /// <summary>The purchase is refused (409 <c>price_above_range</c>).</summary>
    Block,
}
