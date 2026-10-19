using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Prices;

/// <summary>The reference range a purchase is compared with, as found on the purchase date.</summary>
internal sealed record ApplicableRange(int RangeId, int? SupplierId, decimal MinPrice, decimal MaxPrice, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

/// <summary>
/// The outcome of comparing one unit price with one range, with no database access. Calculated on the server for
/// every purchase that names a price item; the client shows it, it never decides it.
/// </summary>
/// <param name="DifferencePerUnit">
/// Above the range: unit price − maximum (positive). Below: unit price − minimum (negative). Inside, or no range: 0.
/// </param>
/// <param name="TotalDifference">round(difference per unit × quantity, 2): what the purchase costs above (or below) the range.</param>
internal sealed record PriceComparison(PriceCheckResult Result, decimal DifferencePerUnit, decimal TotalDifference)
{
    public bool IsAboveRange => Result == PriceCheckResult.AboveRange;
}

internal static class PriceCheck
{
    /// <param name="range"><c>null</c> when no approved range applies on the date.</param>
    public static PriceComparison Compare(decimal unitPrice, decimal quantity, decimal? minPrice, decimal? maxPrice)
    {
        if (minPrice is not { } min || maxPrice is not { } max)
        {
            return new PriceComparison(PriceCheckResult.NoReference, 0, 0);
        }

        if (unitPrice > max)
        {
            var difference = unitPrice - max;
            return new PriceComparison(PriceCheckResult.AboveRange, difference, Money.Round(difference * quantity));
        }

        if (unitPrice < min)
        {
            var difference = unitPrice - min;
            return new PriceComparison(PriceCheckResult.BelowRange, difference, Money.Round(difference * quantity));
        }

        return new PriceComparison(PriceCheckResult.WithinRange, 0, 0);
    }

    /// <summary>
    /// What the policy says about a purchase above the range: <c>null</c> to go ahead, otherwise the refusal. A
    /// justification is required under every policy that lets it through, so the reviewer sees why.
    /// </summary>
    public static PriceCheckRefusal? Refusal(PriceComparison comparison, PriceCheckPolicy policy, string? justification)
    {
        if (!comparison.IsAboveRange)
        {
            return null;
        }

        if (policy == PriceCheckPolicy.Block)
        {
            return PriceCheckRefusal.Blocked;
        }

        return justification is null ? PriceCheckRefusal.NeedsJustification : null;
    }

    /// <summary>Under <see cref="PriceCheckPolicy.ExecutiveApproval"/>, only an Executive approves a purchase above its range.</summary>
    public static bool NeedsExecutive(PriceCheckResult? result, PriceCheckPolicy policy) =>
        result == PriceCheckResult.AboveRange && policy == PriceCheckPolicy.ExecutiveApproval;
}

internal enum PriceCheckRefusal
{
    /// <summary>The policy is Block: 409 <c>price_above_range</c>.</summary>
    Blocked,

    /// <summary>The recorder must say why: 400 on <c>priceJustification</c>.</summary>
    NeedsJustification,
}

/// <summary>The one rule for "which reference range applies to this purchase".</summary>
internal static class PriceRanges
{
    /// <summary>Approved ranges of an item for one supplier (or general, when <paramref name="supplierId"/> is null), started on or before the date, newest first.</summary>
    public static IQueryable<PriceRange> ApprovedOn(IQueryable<PriceRange> ranges, int? supplierId, DateOnly date) =>
        ranges
            .Where(range => range.Status == PriceRangeStatus.Approved && range.SupplierId == supplierId && range.EffectiveFrom <= date)
            .OrderByDescending(range => range.EffectiveFrom)
            .ThenByDescending(range => range.Id);

    /// <summary>
    /// The supplier's own approved range with the latest start on or before <paramref name="date"/>, otherwise the
    /// general one. A newer range replaces an older one; when the newest one has expired, nothing applies (an expired
    /// price is not silently replaced by an even older one).
    /// </summary>
    public static async Task<ApplicableRange?> ApplicableAsync(
        ProjectsDbContext db, int priceItemId, int? supplierId, DateOnly date, CancellationToken cancellationToken)
    {
        var ranges = db.PriceRanges.AsNoTracking().Where(range => range.PriceItemId == priceItemId);

        if (supplierId is not null)
        {
            var own = await FirstAsync(ApprovedOn(ranges, supplierId, date), cancellationToken);
            if (own is not null)
            {
                return own.EffectiveTo is { } end && end < date ? null : own;
            }
        }

        var general = await FirstAsync(ApprovedOn(ranges, null, date), cancellationToken);
        return general is null || (general.EffectiveTo is { } last && last < date) ? null : general;
    }

    private static Task<ApplicableRange?> FirstAsync(IQueryable<PriceRange> ranges, CancellationToken cancellationToken) =>
        ranges
            .Select(range => new ApplicableRange(range.Id, range.SupplierId, range.MinPrice, range.MaxPrice, range.Currency, range.EffectiveFrom, range.EffectiveTo))
            .FirstOrDefaultAsync(cancellationToken)!;
}

/// <summary>
/// The price comparison of a purchase, as every screen returns it: the reference range copied when it was recorded,
/// the actual price, and the differences. <c>null</c> when the purchase names no price item.
/// </summary>
internal sealed record PriceCheckView(
    int PriceItemId,
    string? PriceItemNameAr,
    string? PriceItemNameEn,
    int? PriceRangeId,
    decimal? MinPrice,
    decimal? MaxPrice,
    string Currency,
    string Result,
    decimal UnitPrice,
    decimal Quantity,
    decimal DifferencePerUnit,
    decimal TotalDifference,
    string? Justification)
{
    public static PriceCheckView? From(ExpenseRow row)
    {
        if (row.PriceItemId is not { } priceItemId || row.PriceCheck is not { } result || row.UnitPrice is not { } price || row.Quantity is not { } quantity)
        {
            return null;
        }

        var comparison = PriceCheck.Compare(price, quantity, row.ReferenceMinPrice, row.ReferenceMaxPrice);
        return new PriceCheckView(
            priceItemId, row.PriceItemNameAr, row.PriceItemNameEn, row.PriceRangeId, row.ReferenceMinPrice, row.ReferenceMaxPrice,
            PriceCurrencies.Egp, result.ToString(), price, quantity, comparison.DifferencePerUnit, comparison.TotalDifference, row.PriceJustification);
    }

    public static PriceCheckView Of(
        int priceItemId, string? nameAr, string? nameEn, ApplicableRange? range, decimal unitPrice, decimal quantity, string? justification)
    {
        var comparison = PriceCheck.Compare(unitPrice, quantity, range?.MinPrice, range?.MaxPrice);
        return new PriceCheckView(
            priceItemId, nameAr, nameEn, range?.RangeId, range?.MinPrice, range?.MaxPrice, range?.Currency ?? PriceCurrencies.Egp,
            comparison.Result.ToString(), unitPrice, quantity, comparison.DifferencePerUnit, comparison.TotalDifference, justification);
    }
}
