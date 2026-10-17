using AlMotaheda.BuildingBlocks.Domain;
namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// One definition of the company's master catalog: a material, a service, a piece of work, a labor or an equipment
/// component, with its controlled prices (<see cref="MasterItemPrice"/>). Projects select the definitions they use;
/// selecting links a project record to this row and never copies the catalog (docs/cost-flow §2.2).
/// </summary>
/// <remarks>
/// <see cref="MasterItemKind.Work"/> and <see cref="MasterItemKind.Composite"/> become project items ("بنود") when
/// selected; every other kind becomes a priced project catalog item. The row holds no price: prices are rows.
/// </remarks>
internal sealed class MasterItem
{
    public int Id { get; set; }

    /// <summary>Unique.</summary>
    public required string NameAr { get; set; }

    /// <summary>Unique.</summary>
    public required string NameEn { get; set; }

    public MasterItemKind Kind { get; set; }

    /// <summary>
    /// A <c>WorkTypes</c> value for <see cref="MasterItemKind.Work"/> and <see cref="MasterItemKind.Composite"/>;
    /// a <see cref="CatalogCategory"/> name for the other kinds, so a selected item fits the project table it goes to.
    /// </summary>
    public required string Category { get; set; }

    /// <summary>One of <c>MeasurementUnits</c>. Fixed once a project uses the item.</summary>
    public required string Unit { get; set; }

    public string? Description { get; set; }

    public MasterItemStatus Status { get; set; } = MasterItemStatus.Active;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<MasterItemPrice> Prices { get; } = [];

    /// <summary>True when selecting the item creates a project item ("بند") rather than a priced catalog item.</summary>
    public static bool IsWork(MasterItemKind kind) => kind is MasterItemKind.Work or MasterItemKind.Composite;
}

/// <summary>Stored by name.</summary>
internal enum MasterItemKind
{
    /// <summary>A raw material bought by quantity: cement, sand, aggregate, steel.</summary>
    Material,

    /// <summary>A service bought by quantity or trip: transport, testing.</summary>
    Service,

    /// <summary>A piece of work that collects cost: plumbing, electrical work.</summary>
    Work,

    /// <summary>A piece of work assembled from components: concrete work (cement, sand, aggregate, labor, equipment).</summary>
    Composite,

    /// <summary>A labor cost component with a controlled rate.</summary>
    Labor,

    /// <summary>An equipment cost component with a controlled rate.</summary>
    Equipment,
}

internal enum MasterItemStatus
{
    Active,
    Inactive,
}

/// <summary>
/// One controlled price of a master item from a date on, with the same request and decision flow as a project price
/// (<see cref="ItemPrice"/>): never overwritten, decided once, never by its requester.
/// </summary>
internal sealed class MasterItemPrice
{
    public int Id { get; set; }

    public int MasterItemId { get; set; }
    public MasterItem MasterItem { get; set; } = null!;

    /// <summary>Price per unit, in EGP.</summary>
    public decimal Price { get; set; }

    /// <summary>The current price when this one was requested; empty for an item's first price.</summary>
    public decimal? PreviousPrice { get; set; }

    /// <summary>The first day this price applies.</summary>
    public DateOnly EffectiveFrom { get; set; }

    private ItemPriceStatus _status = ItemPriceStatus.Pending;

    /// <summary>Set when the record is created; afterwards only <see cref="Decide"/> changes it.</summary>
    public ItemPriceStatus Status { get => _status; init => _status = value; }

    public string? Reason { get; set; }

    public DateTime RequestedAt { get; set; }
    public int RequestedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    private static readonly StatusFlow<ItemPriceStatus> Flow = new(
        (ItemPriceStatus.Pending, ItemPriceStatus.Approved),
        (ItemPriceStatus.Pending, ItemPriceStatus.Rejected));

    public void Decide(ItemPriceStatus outcome, int deciderId, DateTime now, string? note)
    {
        _status = Flow.Move(_status, outcome);
        DecidedAt = now;
        DecidedBy = deciderId;
        DecisionNote = note;
    }
}
