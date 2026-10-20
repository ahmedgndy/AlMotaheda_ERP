using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Prices;

/// <summary>The reference range a purchase is compared with, as found on the purchase date.</summary>
internal sealed record ApplicableRange(int RangeId, int? SupplierId, decimal MinPrice, decimal MaxPrice, string Currency, DateOnly EffectiveFrom, DateOnly? EffectiveTo, decimal? ReferencePrice = null);

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

    /// <summary>Above the range: the excess per unit as a percentage of the highest accepted price (2 decimals); otherwise 0.</summary>
    public decimal PercentAbove(decimal? maxPrice) =>
        IsAboveRange && maxPrice is { } max && max > 0 ? decimal.Round(DifferencePerUnit / max * 100, 2, MidpointRounding.AwayFromZero) : 0;
}

/// <summary>
/// The bounds a rule compares with, from how it was expressed: a range keeps its bounds; a fixed price is its
/// reference price, widened by the tolerance (percent) on both sides; a lump sum is a range on the whole amount.
/// </summary>
internal static class PriceRule
{
    public static (decimal MinPrice, decimal MaxPrice) Bounds(PriceRuleKind kind, decimal? minPrice, decimal? maxPrice, decimal? referencePrice, decimal? tolerancePercent)
    {
        if (kind == PriceRuleKind.Fixed && referencePrice is { } reference)
        {
            var tolerance = (tolerancePercent ?? 0) / 100;
            return (Money.Round(reference * (1 - tolerance)), Money.Round(reference * (1 + tolerance)));
        }

        return (minPrice ?? referencePrice ?? 0, maxPrice ?? referencePrice ?? 0);
    }
}

/// <summary>
/// What a price exception does to its transaction, with no database access. Under <see cref="PriceCheckPolicy.ExecutiveApproval"/>
/// a transaction above its range waits for the Executive's decision in Price Control: it can't be approved and nothing
/// can be paid on it until the case is approved; a rejected case keeps it waiting until the submitter corrects it.
/// Under <see cref="PriceCheckPolicy.Warn"/> the case is opened for the inbox and the reports only.
/// </summary>
internal static class PriceGate
{
    /// <summary>Whether cases gate transactions under <paramref name="policy"/>.</summary>
    public static bool Gates(PriceCheckPolicy policy) => policy == PriceCheckPolicy.ExecutiveApproval;

    /// <summary>The refusal to answer when approving a record, or <c>null</c> to go ahead.</summary>
    /// <param name="exception">The record's newest case, or <c>null</c> when it has none.</param>
    /// <param name="legacyAboveRange">The record was compared above its range before cases existed (no case row).</param>
    public static PriceGateRefusal? ApprovalRefusal(PriceExceptionStatus? exception, bool legacyAboveRange, PriceCheckPolicy policy, bool callerIsExecutive)
    {
        if (!Gates(policy))
        {
            return null;
        }

        return exception switch
        {
            PriceExceptionStatus.Pending => PriceGateRefusal.Pending,
            PriceExceptionStatus.Rejected => PriceGateRefusal.Rejected,
            PriceExceptionStatus.ApprovedOnce or PriceExceptionStatus.ApprovedWithPriceChange => null,
            _ => legacyAboveRange && !callerIsExecutive ? PriceGateRefusal.NeedsExecutive : null,
        };
    }

    /// <summary>Money leaves on a record only once its case is approved (or it never had one).</summary>
    public static bool PaymentBlocked(PriceExceptionStatus? exception, PriceCheckPolicy policy) =>
        Gates(policy) && exception is PriceExceptionStatus.Pending or PriceExceptionStatus.Rejected;
}

internal enum PriceGateRefusal
{
    /// <summary>409 <c>price_exception_pending</c>: the Executive has not decided.</summary>
    Pending,

    /// <summary>409 <c>price_exception_rejected</c>: the submitter must correct the price or the reviewer return the record.</summary>
    Rejected,

    /// <summary>403 <c>price_needs_executive</c>: a record from before cases existed; only an Executive approves it.</summary>
    NeedsExecutive,
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
            .Select(range => new ApplicableRange(range.Id, range.SupplierId, range.MinPrice, range.MaxPrice, range.Currency, range.EffectiveFrom, range.EffectiveTo, range.ReferencePrice))
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
    string? Justification,
    decimal? ReferencePrice = null,
    decimal PercentAbove = 0,
    PriceExceptionRef? Exception = null)
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
            PriceCurrencies.Egp, result.ToString(), price, quantity, comparison.DifferencePerUnit, comparison.TotalDifference, row.PriceJustification,
            null, comparison.PercentAbove(row.ReferenceMaxPrice),
            row.ExceptionId is { } exceptionId ? new PriceExceptionRef(exceptionId, row.ExceptionStatus ?? "") : null);
    }

    public static PriceCheckView Of(
        int priceItemId, string? nameAr, string? nameEn, ApplicableRange? range, decimal unitPrice, decimal quantity, string? justification)
    {
        var comparison = PriceCheck.Compare(unitPrice, quantity, range?.MinPrice, range?.MaxPrice);
        return new PriceCheckView(
            priceItemId, nameAr, nameEn, range?.RangeId, range?.MinPrice, range?.MaxPrice, range?.Currency ?? PriceCurrencies.Egp,
            comparison.Result.ToString(), unitPrice, quantity, comparison.DifferencePerUnit, comparison.TotalDifference, justification,
            range?.ReferencePrice, comparison.PercentAbove(range?.MaxPrice));
    }
}

/// <summary>The price case of a record, as its views show it: the case id and its status.</summary>
internal sealed record PriceExceptionRef(int Id, string Status);
