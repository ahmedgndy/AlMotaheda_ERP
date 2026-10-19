using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Prices;

/// <summary>One range of an item: its first range, a change request, or a decided change.</summary>
internal sealed record PriceRangeView(
    int Id,
    int PriceItemId,
    string? PriceItemNameAr,
    string? PriceItemNameEn,
    string? Unit,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    decimal MinPrice,
    decimal MaxPrice,
    string Currency,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? Conditions,
    string Status,
    string? Reason,
    DateTime RequestedAt,
    int RequestedBy,
    string? RequestedByNameAr,
    string? RequestedByNameEn,
    DateTime? DecidedAt,
    int? DecidedBy,
    string? DecidedByNameAr,
    string? DecidedByNameEn,
    string? DecisionNote);

/// <summary>One item of the price list, with the general range that applies today and its open request.</summary>
/// <param name="CurrentRange">The general range on today's date; <c>null</c> when none applies.</param>
/// <param name="SupplierRangeCount">Approved supplier-specific ranges (any date).</param>
internal sealed record PriceItemRow(
    int Id,
    string NameAr,
    string NameEn,
    string Classification,
    string Unit,
    string? Notes,
    string Status,
    PriceRangeView? CurrentRange,
    bool HasPendingChange,
    int SupplierRangeCount,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion);

/// <summary>A price item with its full history, newest first.</summary>
internal sealed record PriceItemDetail(PriceItemRow Item, IReadOnlyList<PriceRangeView> Ranges);

/// <summary>The price list queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class PriceQueries
{
    internal sealed class RangeRow
    {
        public int Id { get; init; }
        public int PriceItemId { get; init; }
        public string? PriceItemNameAr { get; init; }
        public string? PriceItemNameEn { get; init; }
        public string? Unit { get; init; }
        public int? SupplierId { get; init; }
        public string? SupplierNameAr { get; init; }
        public string? SupplierNameEn { get; init; }
        public decimal MinPrice { get; init; }
        public decimal MaxPrice { get; init; }
        public string Currency { get; init; } = PriceCurrencies.Egp;
        public DateOnly EffectiveFrom { get; init; }
        public DateOnly? EffectiveTo { get; init; }
        public string? Conditions { get; init; }
        public PriceRangeStatus Status { get; init; }
        public string? Reason { get; init; }
        public DateTime RequestedAt { get; init; }
        public int RequestedBy { get; init; }
        public DateTime? DecidedAt { get; init; }
        public int? DecidedBy { get; init; }
        public string? DecisionNote { get; init; }

        public PriceRangeView View(IReadOnlyDictionary<int, UserSummary> people)
        {
            var requester = people.GetValueOrDefault(RequestedBy);
            var decider = DecidedBy is { } id ? people.GetValueOrDefault(id) : null;
            return new PriceRangeView(
                Id, PriceItemId, PriceItemNameAr, PriceItemNameEn, Unit, SupplierId, SupplierNameAr, SupplierNameEn, MinPrice, MaxPrice, Currency,
                EffectiveFrom, EffectiveTo, Conditions, Status.ToString(), Reason, RequestedAt, RequestedBy, requester?.NameAr, requester?.NameEn,
                DecidedAt, DecidedBy, decider?.NameAr, decider?.NameEn, DecisionNote);
        }
    }

    internal sealed class ItemRow
    {
        public int Id { get; init; }
        public string NameAr { get; init; } = "";
        public string NameEn { get; init; } = "";
        public CatalogCategory Classification { get; init; }
        public string Unit { get; init; } = "";
        public string? Notes { get; init; }
        public PriceItemStatus Status { get; init; }
        public RangeRow? Current { get; init; }
        public bool HasPending { get; init; }
        public int SupplierRanges { get; init; }
        public DateTime CreatedAt { get; init; }
        public int CreatedBy { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public int? UpdatedBy { get; init; }
        public byte[] RowVersion { get; init; } = [];

        /// <param name="today">The newest general range is current only while it has not expired.</param>
        public PriceItemRow View(IReadOnlyDictionary<int, UserSummary> people, DateOnly today) => new(
            Id, NameAr, NameEn, Classification.ToString(), Unit, Notes, Status.ToString(),
            Current is { } current && (current.EffectiveTo is null || current.EffectiveTo >= today) ? current.View(people) : null,
            HasPending, SupplierRanges, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, RowVersions.Format(RowVersion));
    }

    public static IQueryable<RangeRow> Ranges(IQueryable<PriceRange> ranges) =>
        ranges.Select(range => new RangeRow
        {
            Id = range.Id,
            PriceItemId = range.PriceItemId,
            PriceItemNameAr = range.PriceItem.NameAr,
            PriceItemNameEn = range.PriceItem.NameEn,
            Unit = range.PriceItem.Unit,
            SupplierId = range.SupplierId,
            SupplierNameAr = range.Supplier != null ? range.Supplier.NameAr : null,
            SupplierNameEn = range.Supplier != null ? range.Supplier.NameEn : null,
            MinPrice = range.MinPrice,
            MaxPrice = range.MaxPrice,
            Currency = range.Currency,
            EffectiveFrom = range.EffectiveFrom,
            EffectiveTo = range.EffectiveTo,
            Conditions = range.Conditions,
            Status = range.Status,
            Reason = range.Reason,
            RequestedAt = range.RequestedAt,
            RequestedBy = range.RequestedBy,
            DecidedAt = range.DecidedAt,
            DecidedBy = range.DecidedBy,
            DecisionNote = range.DecisionNote,
        });

    /// <summary>
    /// The price list rows with the general range on <paramref name="today"/> (the rule of
    /// <see cref="PriceRanges.ApprovedOn"/>, written out so it translates inside the projection). An expired newest
    /// range is returned and dropped in memory, like <see cref="PriceRanges.ApplicableAsync"/>.
    /// </summary>
    public static IQueryable<ItemRow> Items(ProjectsDbContext db, IQueryable<PriceItem> items, DateOnly today) =>
        items.Select(item => new ItemRow
        {
            Id = item.Id,
            NameAr = item.NameAr,
            NameEn = item.NameEn,
            Classification = item.Classification,
            Unit = item.Unit,
            Notes = item.Notes,
            Status = item.Status,
            Current = Ranges(db.PriceRanges
                    .Where(range => range.PriceItemId == item.Id && range.Status == PriceRangeStatus.Approved && range.SupplierId == null && range.EffectiveFrom <= today)
                    .OrderByDescending(range => range.EffectiveFrom)
                    .ThenByDescending(range => range.Id))
                .FirstOrDefault(),
            HasPending = db.PriceRanges.Any(range => range.PriceItemId == item.Id && range.Status == PriceRangeStatus.Pending),
            SupplierRanges = db.PriceRanges.Count(range => range.PriceItemId == item.Id && range.Status == PriceRangeStatus.Approved && range.SupplierId != null),
            CreatedAt = item.CreatedAt,
            CreatedBy = item.CreatedBy,
            UpdatedAt = item.UpdatedAt,
            UpdatedBy = item.UpdatedBy,
            RowVersion = item.RowVersion,
        });

    /// <summary>One item with its history, or <c>null</c>.</summary>
    public static async Task<PriceItemDetail?> DetailAsync(ProjectsDbContext db, IUserDirectory users, int priceItemId, DateOnly today, CancellationToken cancellationToken)
    {
        var item = await Items(db, db.PriceItems.AsNoTracking().Where(candidate => candidate.Id == priceItemId), today).FirstOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return null;
        }

        var ranges = await Ranges(db.PriceRanges.AsNoTracking().Where(range => range.PriceItemId == priceItemId))
            .OrderByDescending(range => range.EffectiveFrom)
            .ThenByDescending(range => range.Id)
            .ToListAsync(cancellationToken);

        var people = await PlanningPeople.FindAsync(
            users,
            ranges.SelectMany(range => new[] { range.RequestedBy, range.DecidedBy ?? range.RequestedBy }),
            cancellationToken);

        return new PriceItemDetail(item.View(people, today), ranges.Select(range => range.View(people)).ToList());
    }
}
