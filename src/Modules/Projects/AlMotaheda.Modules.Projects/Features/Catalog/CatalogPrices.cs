using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Catalog;

/// <summary>
/// The one rule for "what does this catalog item cost on this date": the approved price with the latest start
/// date on or before the date. Shared by the catalog screens and by supplies, which copy the price they find.
/// </summary>
internal static class CatalogPrices
{
    /// <summary>Approved prices that already apply on <paramref name="date"/>, newest first.</summary>
    public static IQueryable<ItemPrice> ApprovedOn(IQueryable<ItemPrice> prices, DateOnly date) =>
        prices
            .Where(price => price.Status == ItemPriceStatus.Approved && price.EffectiveFrom <= date)
            .OrderByDescending(price => price.EffectiveFrom)
            .ThenByDescending(price => price.Id);

    /// <summary>
    /// The reference price of the item on the date: the project's own approved price, or, when it has none in force
    /// and the item was selected from the master catalog, the master's approved price (docs/cost-flow §2.2).
    /// <c>null</c> when no approved price applies yet.
    /// </summary>
    public static async Task<decimal?> CurrentAsync(ProjectsDbContext db, int catalogItemId, DateOnly date, CancellationToken cancellationToken)
    {
        var own = await ApprovedOn(db.ItemPrices.AsNoTracking().Where(price => price.CatalogItemId == catalogItemId), date)
            .Select(price => (decimal?)price.Price)
            .FirstOrDefaultAsync(cancellationToken);
        if (own is not null)
        {
            return own;
        }

        return await MasterApprovedOn(
                db.MasterItemPrices.AsNoTracking()
                    .Where(price => db.CatalogItems.Any(item => item.Id == catalogItemId && item.MasterItemId == price.MasterItemId)),
                date)
            .Select(price => (decimal?)price.Price)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The same rule for the master catalog: approved master prices that already apply, newest first.</summary>
    public static IQueryable<MasterItemPrice> MasterApprovedOn(IQueryable<MasterItemPrice> prices, DateOnly date) =>
        prices
            .Where(price => price.Status == ItemPriceStatus.Approved && price.EffectiveFrom <= date)
            .OrderByDescending(price => price.EffectiveFrom)
            .ThenByDescending(price => price.Id);
}
