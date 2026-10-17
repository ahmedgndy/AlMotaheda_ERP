using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Projects;

namespace AlMotaheda.Modules.Projects.Features.Catalog;

/// <summary>
/// The permission module of the price catalog: View to see items and prices, Create to add and edit items, Edit to
/// request a price change, Approve to decide one (re-checked in the database), See money for estimated values and
/// amounts bought.
/// </summary>
internal static class CatalogPermissions
{
    public const string Module = "catalog";
}

/// <summary>One price row: a first price, a change request, or a decided change.</summary>
/// <param name="ChangePercent">From <paramref name="PreviousPrice"/> to <paramref name="Price"/>; <c>null</c> for a first price.</param>
internal sealed record PriceChangeView(
    int Id,
    int CatalogItemId,
    string? ItemNameAr,
    string? ItemNameEn,
    decimal Price,
    decimal? PreviousPrice,
    decimal? ChangePercent,
    DateOnly EffectiveFrom,
    string Status,
    string? Reason,
    DateTime RequestedAt,
    PersonRef Requester,
    DateTime? DecidedAt,
    PersonRef? Decider,
    string? DecisionNote)
{
    public static PriceChangeView From(PriceRow row, string? itemNameAr, string? itemNameEn, IReadOnlyDictionary<int, UserSummary> people) =>
        new(
            row.Id,
            row.CatalogItemId,
            itemNameAr,
            itemNameEn,
            row.Price,
            row.PreviousPrice,
            CatalogPricing.ChangePercent(row.PreviousPrice, row.Price),
            row.EffectiveFrom,
            row.Status.ToString(),
            row.Reason,
            row.RequestedAt,
            PersonRef.From(row.RequestedBy, people),
            row.DecidedAt,
            row.DecidedBy is { } decider ? PersonRef.From(decider, people) : null,
            row.DecisionNote);

    /// <summary>The users named on the rows, for one name lookup.</summary>
    public static IEnumerable<int> People(IEnumerable<PriceRow> rows) =>
        rows.SelectMany(row => row.DecidedBy is { } decider ? new[] { row.RequestedBy, decider } : [row.RequestedBy]);
}

/// <summary>An approved price that starts after today.</summary>
internal sealed record ScheduledPriceView(int PriceId, decimal Price, DateOnly EffectiveFrom);

/// <summary>
/// One catalog item with where its price stands today. Prices are shown to everyone with catalog View. The
/// estimated value and the amount bought are money: <c>null</c> without catalog See money.
/// </summary>
internal sealed record CatalogItemView(
    int Id,
    int ProjectId,
    string NameAr,
    string NameEn,
    string Category,
    string Unit,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    string Status,
    int? MasterItemId,
    decimal? CurrentPrice,
    string? PriceSource,
    DateOnly? CurrentPriceFrom,
    decimal? PreviousPrice,
    decimal? ChangePercent,
    string PriceStatus,
    PriceChangeView? PendingChange,
    ScheduledPriceView? ScheduledPrice,
    decimal EstimatedQuantity,
    decimal? EstimatedValue,
    decimal QuantityBought,
    decimal? AmountBought,
    int PurchaseCount,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public static CatalogItemView From(
        CatalogItemHeader header,
        IEnumerable<PriceRow> prices,
        IEnumerable<PriceRow> masterPrices,
        IEnumerable<PurchaseRow> purchases,
        DateOnly today,
        bool seesMoney,
        IReadOnlyDictionary<int, UserSummary> people)
    {
        var rows = prices.ToList();
        var position = CatalogPricing.Position(rows, today);
        var reference = CatalogPricing.Reference(rows, masterPrices, today);
        var bought = CatalogPricing.Purchases(purchases, header.Unit);

        return new CatalogItemView(
            header.Id,
            header.ProjectId,
            header.NameAr,
            header.NameEn,
            header.Category.ToString(),
            header.Unit,
            header.SupplierId,
            header.SupplierNameAr,
            header.SupplierNameEn,
            header.Status.ToString(),
            header.MasterItemId,
            reference.Current?.Price,
            reference.Source,
            reference.Current?.EffectiveFrom,
            reference.Previous?.Price,
            reference.ChangePercent,
            position.PriceStatus,
            position.Pending is { } pending ? PriceChangeView.From(pending, header.NameAr, header.NameEn, people) : null,
            position.Scheduled is { } scheduled ? new ScheduledPriceView(scheduled.Id, scheduled.Price, scheduled.EffectiveFrom) : null,
            header.EstimatedQuantity,
            seesMoney ? CatalogPricing.EstimatedValue(header.EstimatedQuantity, reference.Current?.Price) : null,
            bought.Quantity,
            seesMoney ? bought.Amount : null,
            bought.Count,
            header.CreatedAt,
            header.CreatedBy,
            header.UpdatedAt,
            header.UpdatedBy,
            RowVersions.Format(header.RowVersion));
    }

    /// <summary>Builds the views of some items from the project's loaded prices and purchases.</summary>
    public static IReadOnlyList<CatalogItemView> FromRows(
        IReadOnlyList<CatalogItemHeader> headers,
        IReadOnlyList<PriceRow> prices,
        IReadOnlyList<PriceRow> masterPrices,
        IReadOnlyList<PurchaseRow> purchases,
        DateOnly today,
        bool seesMoney,
        IReadOnlyDictionary<int, UserSummary> people)
    {
        var pricesByItem = prices.ToLookup(price => price.CatalogItemId);
        var masterByItem = masterPrices.ToLookup(price => price.CatalogItemId);
        var purchasesByItem = purchases.ToLookup(purchase => purchase.CatalogItemId);

        return headers
            .Select(header => From(header, pricesByItem[header.Id], masterByItem[header.Id], purchasesByItem[header.Id], today, seesMoney, people))
            .ToList();
    }
}

/// <summary>One supply of a catalog item. The unit price is a price (catalog View); the amount needs See money.</summary>
internal sealed record CatalogSupplyView(
    int ExpenseId,
    DateOnly ExpenseDate,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    decimal? Quantity,
    string? Unit,
    decimal? UnitPrice,
    decimal? Amount,
    decimal? CatalogPriceAtRecording,
    string Status)
{
    public static CatalogSupplyView From(CatalogSupplyRow row, bool seesMoney) => new(
        row.ExpenseId,
        row.ExpenseDate,
        row.SupplierId,
        row.SupplierNameAr,
        row.SupplierNameEn,
        row.Quantity,
        row.Unit,
        row.UnitPrice,
        seesMoney ? row.Amount : null,
        row.CatalogPriceAtRecording,
        row.Status.ToString());
}

/// <summary>
/// The figures at the top of the catalog screen, for the whole project whatever the filters. Money figures are
/// <c>null</c> without catalog See money.
/// </summary>
internal sealed record CatalogSummary(int ItemCount, decimal? EstimatedValue, decimal? AmountBought, int PendingChangeCount)
{
    public static CatalogSummary Compute(
        IReadOnlyList<CatalogItemEstimate> items,
        IReadOnlyList<PriceRow> prices,
        IReadOnlyList<PurchaseRow> purchases,
        DateOnly today,
        bool seesMoney,
        IReadOnlyList<PriceRow>? masterPrices = null)
    {
        var pricesByItem = prices.ToLookup(price => price.CatalogItemId);
        var masterByItem = (masterPrices ?? []).ToLookup(price => price.CatalogItemId);
        var estimated = items.Sum(item =>
            CatalogPricing.EstimatedValue(
                item.EstimatedQuantity, CatalogPricing.Reference(pricesByItem[item.Id], masterByItem[item.Id], today).Current?.Price) ?? 0);

        return new CatalogSummary(
            items.Count,
            seesMoney ? estimated : null,
            seesMoney ? purchases.Sum(purchase => purchase.Amount) : null,
            prices.Count(price => price.Status == ItemPriceStatus.Pending));
    }
}
