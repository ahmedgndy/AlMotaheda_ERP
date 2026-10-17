using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Catalog;

/// <summary>One catalog item without prices, with its usual supplier's name.</summary>
internal sealed record CatalogItemHeader(
    int Id,
    int ProjectId,
    string NameAr,
    string NameEn,
    CatalogCategory Category,
    string Unit,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    decimal EstimatedQuantity,
    CatalogItemStatus Status,
    int? MasterItemId,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    byte[] RowVersion);

/// <summary>A price change with the names of its item, for the approval queue.</summary>
internal sealed record PriceChangeRow(PriceRow Price, string ItemNameAr, string ItemNameEn);

/// <summary>One supply of a catalog item, for the item's detail.</summary>
internal sealed record CatalogSupplyRow(
    int ExpenseId,
    DateOnly ExpenseDate,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    decimal? Quantity,
    string? Unit,
    decimal? UnitPrice,
    decimal Amount,
    decimal? CatalogPriceAtRecording,
    ExpenseStatus Status);

/// <summary>The filters of <c>GET /api/projects/{pid}/catalog-items</c>, after validation.</summary>
internal sealed record CatalogFilter(CatalogCategory? Category, CatalogItemStatus? Status, string? PriceStatus, string? Search);

/// <summary>The catalog queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class CatalogQueries
{
    /// <summary>
    /// The list screen. Every filter runs in SQL, the price status too: a pending request wins, then an approved
    /// price starting after <paramref name="today"/>, else the item is <c>Active</c>.
    /// </summary>
    public static IQueryable<CatalogItemHeader> Filtered(ProjectsDbContext db, int projectId, CatalogFilter filter, DateOnly today)
    {
        var query = db.CatalogItems.AsNoTracking().Where(item => item.ProjectId == projectId);

        if (filter.Category is { } category)
        {
            query = query.Where(item => item.Category == category);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(item => item.Status == status);
        }

        if (filter.Search is { } text)
        {
            query = query.Where(item => item.NameEn.Contains(text) || item.NameAr.Contains(text));
        }

        switch (filter.PriceStatus)
        {
            case CatalogPriceStatuses.PendingChange:
                query = query.Where(item => item.Prices.Any(price => price.Status == ItemPriceStatus.Pending));
                break;
            case CatalogPriceStatuses.Scheduled:
                query = query.Where(item => !item.Prices.Any(price => price.Status == ItemPriceStatus.Pending)
                    && item.Prices.Any(price => price.Status == ItemPriceStatus.Approved && price.EffectiveFrom > today));
                break;
            case CatalogPriceStatuses.Active:
                query = query.Where(item => !item.Prices.Any(price => price.Status == ItemPriceStatus.Pending)
                    && !item.Prices.Any(price => price.Status == ItemPriceStatus.Approved && price.EffectiveFrom > today));
                break;
        }

        return Project(query.OrderBy(item => item.NameEn).ThenBy(item => item.Id));
    }

    /// <summary>One item of the project, or none when the id belongs to another project.</summary>
    public static IQueryable<CatalogItemHeader> One(ProjectsDbContext db, int projectId, int itemId) =>
        Project(db.CatalogItems.AsNoTracking().Where(item => item.ProjectId == projectId && item.Id == itemId));

    /// <summary>Every price row of the project's catalog (or of one item), flat.</summary>
    public static IQueryable<PriceRow> Prices(ProjectsDbContext db, int projectId, int? itemId = null) =>
        db.ItemPrices.AsNoTracking()
            .Where(price => price.CatalogItem.ProjectId == projectId && (itemId == null || price.CatalogItemId == itemId))
            .Select(price => new PriceRow(
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
                price.DecisionNote));

    /// <summary>
    /// The approved master prices of the project's items that were selected from the master catalog (or of one item),
    /// as rows of the project item (<see cref="PriceRow.CatalogItemId"/> is the project's catalog item). They apply
    /// only while the project has no approved price of its own (<see cref="CatalogPricing.Reference"/>).
    /// </summary>
    public static IQueryable<PriceRow> MasterPrices(ProjectsDbContext db, int projectId, int? itemId = null) =>
        from item in db.CatalogItems.AsNoTracking()
        where item.ProjectId == projectId && item.MasterItemId != null && (itemId == null || item.Id == itemId)
        from price in db.MasterItemPrices.AsNoTracking()
        where price.MasterItemId == item.MasterItemId && price.Status == ItemPriceStatus.Approved
        select new PriceRow(
            price.Id,
            item.Id,
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

    /// <summary>
    /// The approved supplies of the project that name a catalog item (or one item): what was really bought.
    /// A recorded, returned or rejected supply was not bought yet, or not at all.
    /// </summary>
    public static IQueryable<PurchaseRow> Purchases(ProjectsDbContext db, int projectId, int? itemId = null) =>
        db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.ProjectId == projectId
                && expense.Kind == ExpenseKind.Supply
                && expense.Status == ExpenseStatus.Approved
                && expense.CatalogItemId != null
                && (itemId == null || expense.CatalogItemId == itemId))
            .Select(expense => new PurchaseRow(expense.CatalogItemId!.Value, expense.Unit, expense.Quantity, expense.Amount));

    /// <summary>Every item of the project, for the summary: what each estimate is made of.</summary>
    public static IQueryable<CatalogItemEstimate> Estimates(ProjectsDbContext db, int projectId) =>
        db.CatalogItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId)
            .Select(item => new CatalogItemEstimate(item.Id, item.Unit, item.EstimatedQuantity));

    /// <summary>The newest supplies of one item, whatever their status, newest first.</summary>
    public static IQueryable<CatalogSupplyRow> RecentSupplies(ProjectsDbContext db, int projectId, int itemId, int count) =>
        db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.ProjectId == projectId && expense.CatalogItemId == itemId && expense.Kind == ExpenseKind.Supply)
            .OrderByDescending(expense => expense.ExpenseDate)
            .ThenByDescending(expense => expense.Id)
            .Take(count)
            .Select(expense => new CatalogSupplyRow(
                expense.Id,
                expense.ExpenseDate,
                expense.SupplierId,
                expense.Supplier != null ? expense.Supplier.NameAr : null,
                expense.Supplier != null ? expense.Supplier.NameEn : null,
                expense.Quantity,
                expense.Unit,
                expense.UnitPrice,
                expense.Amount,
                expense.CatalogPriceAtRecording,
                expense.Status));

    /// <summary>
    /// The price change requests of the project, newest first. An item's first price is not a request (it has no
    /// reason and was approved when the item was added), so it is left out.
    /// </summary>
    public static IQueryable<PriceChangeRow> Changes(ProjectsDbContext db, int projectId, ItemPriceStatus? status)
    {
        var query = db.ItemPrices.AsNoTracking()
            .Where(price => price.CatalogItem.ProjectId == projectId && price.Reason != null);

        if (status is { } wanted)
        {
            query = query.Where(price => price.Status == wanted);
        }

        return query
            .OrderByDescending(price => price.RequestedAt)
            .ThenByDescending(price => price.Id)
            .Select(price => new PriceChangeRow(
                new PriceRow(
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
                    price.DecisionNote),
                price.CatalogItem.NameAr,
                price.CatalogItem.NameEn));
    }

    private static IQueryable<CatalogItemHeader> Project(IQueryable<CatalogItem> items) =>
        items.Select(item => new CatalogItemHeader(
            item.Id,
            item.ProjectId,
            item.NameAr,
            item.NameEn,
            item.Category,
            item.Unit,
            item.SupplierId,
            item.Supplier != null ? item.Supplier.NameAr : null,
            item.Supplier != null ? item.Supplier.NameEn : null,
            item.EstimatedQuantity,
            item.Status,
            item.MasterItemId,
            item.CreatedAt,
            item.CreatedBy,
            item.UpdatedAt,
            item.UpdatedBy,
            item.RowVersion));
}

/// <summary>One catalog item's estimate inputs, for the summary.</summary>
internal sealed record CatalogItemEstimate(int Id, string Unit, decimal EstimatedQuantity);
