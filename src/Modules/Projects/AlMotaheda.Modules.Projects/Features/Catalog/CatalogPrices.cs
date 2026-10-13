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

    /// <summary>The price of the item on the date, or <c>null</c> when no approved price applies yet.</summary>
    public static Task<decimal?> CurrentAsync(ProjectsDbContext db, int catalogItemId, DateOnly date, CancellationToken cancellationToken) =>
        ApprovedOn(db.ItemPrices.AsNoTracking().Where(price => price.CatalogItemId == catalogItemId), date)
            .Select(price => (decimal?)price.Price)
            .FirstOrDefaultAsync(cancellationToken);
}
