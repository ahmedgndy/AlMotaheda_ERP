using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;

namespace AlMotaheda.Modules.Projects.Features.Catalog;

/// <summary>One price row of a catalog item, flat, as the pricing rules see it.</summary>
internal sealed record PriceRow(
    int Id,
    int CatalogItemId,
    decimal Price,
    decimal? PreviousPrice,
    DateOnly EffectiveFrom,
    ItemPriceStatus Status,
    string? Reason,
    DateTime RequestedAt,
    int RequestedBy,
    DateTime? DecidedAt,
    int? DecidedBy,
    string? DecisionNote)
{
    /// <summary>The row of a price just saved, without reading it back.</summary>
    public static PriceRow From(ItemPrice price) => new(
        price.Id,
        price.CatalogItemId,
        price.Price,
        price.PreviousPrice,
        price.EffectiveFrom,
        price.Status,
        price.Reason,
        price.RequestedAt,
        price.RequestedBy,
        price.DecidedAt,
        price.DecidedBy,
        price.DecisionNote);
}

/// <summary>One approved supply linked to a catalog item: what was really bought.</summary>
internal sealed record PurchaseRow(int CatalogItemId, string? Unit, decimal? Quantity, decimal Amount);

/// <summary>The price status of a catalog item, as the list filters it.</summary>
internal static class CatalogPriceStatuses
{
    /// <summary>The current price applies and nothing is waiting or planned.</summary>
    public const string Active = "Active";

    /// <summary>A price change waits for an Executive.</summary>
    public const string PendingChange = "PendingChange";

    /// <summary>An approved price starts on a later date; it becomes current by itself on that date.</summary>
    public const string Scheduled = "Scheduled";

    public static IReadOnlyList<string> All { get; } = [Active, PendingChange, Scheduled];
}

/// <summary>Where an item's price stands today.</summary>
/// <param name="Current">The approved price with the latest start date on or before today.</param>
/// <param name="Previous">The approved price that <paramref name="Current"/> replaced (the latest one starting before it).</param>
/// <param name="ChangePercent">From <paramref name="Previous"/> to <paramref name="Current"/>, to two decimals.</param>
/// <param name="Pending">The request waiting for an Executive, if any (at most one).</param>
/// <param name="Scheduled">The next approved price with a start date after today, if any.</param>
internal sealed record PricePosition(
    PriceRow? Current,
    PriceRow? Previous,
    decimal? ChangePercent,
    PriceRow? Pending,
    PriceRow? Scheduled,
    string PriceStatus);

/// <summary>Where the reference price of an item comes from.</summary>
internal static class ReferencePriceSources
{
    /// <summary>The project's own approved price.</summary>
    public const string Project = "Project";

    /// <summary>The master catalog's approved price (the project has none of its own in force).</summary>
    public const string Master = "Master";
}

/// <summary>The price that applies to an item today and where it comes from (<c>null</c> when none applies).</summary>
internal sealed record ReferencePrice(PriceRow? Current, PriceRow? Previous, decimal? ChangePercent, string? Source);

/// <summary>What was bought of one item: approved supplies only.</summary>
/// <param name="Quantity">Only supplies in the item's own unit are added; other units can't be summed with it.</param>
internal sealed record PurchaseTotals(decimal Quantity, decimal Amount, int Count)
{
    public static PurchaseTotals Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// The price rules of the catalog (02-projects-planning.md, section B). Pure: the same rule as
/// <see cref="CatalogPrices"/> (the approved price with the latest start date on or before the date), applied to rows
/// already loaded, so the list, the detail and the summary can't disagree.
/// </summary>
internal static class CatalogPricing
{
    public static PricePosition Position(IEnumerable<PriceRow> prices, DateOnly today)
    {
        var rows = prices.ToList();
        var approved = rows
            .Where(row => row.Status == ItemPriceStatus.Approved)
            .OrderByDescending(row => row.EffectiveFrom)
            .ThenByDescending(row => row.Id)
            .ToList();

        var current = approved.FirstOrDefault(row => row.EffectiveFrom <= today);
        var previous = current is null ? null : approved.FirstOrDefault(row => row.EffectiveFrom < current.EffectiveFrom);
        var scheduled = approved.Where(row => row.EffectiveFrom > today).OrderBy(row => row.EffectiveFrom).ThenBy(row => row.Id).FirstOrDefault();
        var pending = rows.FirstOrDefault(row => row.Status == ItemPriceStatus.Pending);

        var status = pending is not null ? CatalogPriceStatuses.PendingChange
            : scheduled is not null ? CatalogPriceStatuses.Scheduled
            : CatalogPriceStatuses.Active;

        return new PricePosition(
            current,
            previous,
            current is not null && previous is not null ? ChangePercent(previous.Price, current.Price) : null,
            pending,
            scheduled,
            status);
    }

    /// <summary>
    /// The reference price of a project catalog item: the project's own approved price when it has one in force,
    /// otherwise the master catalog's approved price (docs/cost-flow §2.2). <paramref name="masterPrices"/> are the
    /// master's approved rows; empty for an item not selected from the master catalog.
    /// </summary>
    public static ReferencePrice Reference(IEnumerable<PriceRow> projectPrices, IEnumerable<PriceRow> masterPrices, DateOnly today)
    {
        var own = Position(projectPrices, today);
        if (own.Current is not null)
        {
            return new ReferencePrice(own.Current, own.Previous, own.ChangePercent, ReferencePriceSources.Project);
        }

        var master = Position(masterPrices, today);
        return master.Current is null
            ? new ReferencePrice(null, null, null, null)
            : new ReferencePrice(master.Current, master.Previous, master.ChangePercent, ReferencePriceSources.Master);
    }

    /// <summary>(new − old) ÷ old × 100, to two decimals. Positive means the price went up.</summary>
    public static decimal? ChangePercent(decimal? oldPrice, decimal newPrice) =>
        oldPrice is { } old && old != 0 ? Math.Round((newPrice - old) / old * 100, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>Estimated quantity × current price, to the piastre. <c>null</c> while the item has no current price.</summary>
    public static decimal? EstimatedValue(decimal estimatedQuantity, decimal? currentPrice) =>
        currentPrice is { } price ? Money.Round(estimatedQuantity * price) : null;

    /// <summary>What was bought of the item, from its approved supplies.</summary>
    public static PurchaseTotals Purchases(IEnumerable<PurchaseRow> purchases, string itemUnit)
    {
        decimal quantity = 0, amount = 0;
        var count = 0;
        foreach (var purchase in purchases)
        {
            count++;
            amount += purchase.Amount;
            if (purchase.Unit == itemUnit && purchase.Quantity is { } bought)
            {
                quantity += bought;
            }
        }

        return new PurchaseTotals(quantity, amount, count);
    }
}
