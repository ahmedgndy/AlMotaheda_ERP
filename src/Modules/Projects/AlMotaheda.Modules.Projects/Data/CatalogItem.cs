namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// An item of a project's price catalog ("البنود والأسعار"): something the project buys at a known price, such as
/// cement per bag. Its prices are <see cref="ItemPrice"/> rows; the item itself holds no price.
/// </summary>
/// <remarks>
/// Not the same as a <see cref="ProjectItem"/>: a project item is a piece of work that collects cost; a catalog item
/// is a priced material or service.
/// </remarks>
internal sealed class CatalogItem
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public required string NameAr { get; set; }

    /// <summary>Unique within the project.</summary>
    public required string NameEn { get; set; }

    public CatalogCategory Category { get; set; } = CatalogCategory.Other;

    /// <summary>One of <c>MeasurementUnits</c> (Projects.Contracts).</summary>
    public required string Unit { get; set; }

    /// <summary>The usual supplier, if any. Only a default for the supply form.</summary>
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>The quantity the project expects to need (from the bill of quantities). An estimate, never a cost.</summary>
    public decimal EstimatedQuantity { get; set; }

    public CatalogItemStatus Status { get; set; } = CatalogItemStatus.Active;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<ItemPrice> Prices { get; } = [];
}

/// <summary>Stored by name.</summary>
internal enum CatalogCategory
{
    CementAggregates,
    ReadyMix,
    Steel,
    Masonry,
    Finishing,
    Waterproofing,
    Piling,
    Formwork,
    Consumables,
    Services,
    Other,
}

internal enum CatalogItemStatus
{
    Active,
    Inactive,
}

/// <summary>
/// One price of a catalog item from a date on. Prices are never overwritten: a new price is a new row, and old
/// rows keep the history. A row changes exactly once, when a pending request is approved or rejected.
/// </summary>
internal sealed class ItemPrice
{
    public int Id { get; set; }

    public int CatalogItemId { get; set; }
    public CatalogItem CatalogItem { get; set; } = null!;

    /// <summary>Price per unit, in EGP.</summary>
    public decimal Price { get; set; }

    /// <summary>The current price when this one was requested; empty for an item's first price.</summary>
    public decimal? PreviousPrice { get; set; }

    /// <summary>The first day this price applies. May be in the future: it then becomes current on that day.</summary>
    public DateOnly EffectiveFrom { get; set; }

    public ItemPriceStatus Status { get; set; } = ItemPriceStatus.Pending;

    public string? Reason { get; set; }

    public DateTime RequestedAt { get; set; }
    public int RequestedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }
}

internal enum ItemPriceStatus
{
    /// <summary>Waiting for an Executive. Not used by anything yet.</summary>
    Pending,
    Approved,
    Rejected,
}
