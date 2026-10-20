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
    string? DecisionNote,
    string Kind = "Range",
    decimal? ReferencePrice = null,
    decimal? TolerancePercent = null,
    string? SourceNote = null);

/// <summary>A category of the price list.</summary>
internal sealed record PriceCategoryView(int Id, string Code, string NameAr, string NameEn, bool IsActive, int SortOrder, string RowVersion)
{
    public static PriceCategoryView From(PriceCategory category) =>
        new(category.Id, category.Code, category.NameAr, category.NameEn, category.IsActive, category.SortOrder, RowVersions.Format(category.RowVersion));
}

/// <summary>A unit prices are quoted per.</summary>
internal sealed record PriceUnitView(int Id, string Code, string NameAr, string NameEn, bool IsActive, int SortOrder, string RowVersion)
{
    public static PriceUnitView From(PriceUnit unit) =>
        new(unit.Id, unit.Code, unit.NameAr, unit.NameEn, unit.IsActive, unit.SortOrder, RowVersions.Format(unit.RowVersion));
}

/// <summary>One item of the price list, with the general range that applies today and its open request.</summary>
/// <param name="Classification">The category code (the field keeps its old name).</param>
/// <param name="OpenExceptions">Cases about this item waiting for a decision.</param>
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
    string RowVersion,
    string? CategoryNameAr = null,
    string? CategoryNameEn = null,
    int OpenExceptions = 0);

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
        public PriceRuleKind Kind { get; init; }
        public decimal? ReferencePrice { get; init; }
        public decimal? TolerancePercent { get; init; }
        public string? SourceNote { get; init; }

        public PriceRangeView View(IReadOnlyDictionary<int, UserSummary> people)
        {
            var requester = people.GetValueOrDefault(RequestedBy);
            var decider = DecidedBy is { } id ? people.GetValueOrDefault(id) : null;
            return new PriceRangeView(
                Id, PriceItemId, PriceItemNameAr, PriceItemNameEn, Unit, SupplierId, SupplierNameAr, SupplierNameEn, MinPrice, MaxPrice, Currency,
                EffectiveFrom, EffectiveTo, Conditions, Status.ToString(), Reason, RequestedAt, RequestedBy, requester?.NameAr, requester?.NameEn,
                DecidedAt, DecidedBy, decider?.NameAr, decider?.NameEn, DecisionNote, Kind.ToString(), ReferencePrice, TolerancePercent, SourceNote);
        }
    }

    internal sealed class ItemRow
    {
        public int Id { get; init; }
        public string NameAr { get; init; } = "";
        public string NameEn { get; init; } = "";
        public string CategoryCode { get; init; } = "";
        public string CategoryNameAr { get; init; } = "";
        public string CategoryNameEn { get; init; } = "";
        public string Unit { get; init; } = "";
        public string? Notes { get; init; }
        public PriceItemStatus Status { get; init; }

        /// <summary>The newest general range started by the date, as found in SQL; its row is loaded by <see cref="WithCurrentAsync"/>.</summary>
        public int? CurrentRangeId { get; init; }

        public RangeRow? Current { get; set; }
        public bool HasPending { get; init; }
        public int SupplierRanges { get; init; }
        public int OpenExceptions { get; init; }
        public DateTime CreatedAt { get; init; }
        public int CreatedBy { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public int? UpdatedBy { get; init; }
        public byte[] RowVersion { get; init; } = [];

        /// <param name="today">The newest general range is current only while it has not expired.</param>
        public PriceItemRow View(IReadOnlyDictionary<int, UserSummary> people, DateOnly today) => new(
            Id, NameAr, NameEn, CategoryCode, Unit, Notes, Status.ToString(),
            Current is { } current && (current.EffectiveTo is null || current.EffectiveTo >= today) ? current.View(people) : null,
            HasPending, SupplierRanges, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, RowVersions.Format(RowVersion), CategoryNameAr, CategoryNameEn, OpenExceptions);
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
            Kind = range.Kind,
            ReferencePrice = range.ReferencePrice,
            TolerancePercent = range.TolerancePercent,
            SourceNote = range.SourceNote,
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
            CategoryCode = item.Category.Code,
            CategoryNameAr = item.Category.NameAr,
            CategoryNameEn = item.Category.NameEn,
            Unit = item.Unit,
            Notes = item.Notes,
            Status = item.Status,
            CurrentRangeId = db.PriceRanges
                .Where(range => range.PriceItemId == item.Id && range.Status == PriceRangeStatus.Approved && range.SupplierId == null && range.EffectiveFrom <= today)
                .OrderByDescending(range => range.EffectiveFrom)
                .ThenByDescending(range => range.Id)
                .Select(range => (int?)range.Id)
                .FirstOrDefault(),
            HasPending = db.PriceRanges.Any(range => range.PriceItemId == item.Id && range.Status == PriceRangeStatus.Pending),
            SupplierRanges = db.PriceRanges.Count(range => range.PriceItemId == item.Id && range.Status == PriceRangeStatus.Approved && range.SupplierId != null),
            OpenExceptions = db.PriceExceptions.Count(exception => exception.PriceItemId == item.Id && exception.Status == PriceExceptionStatus.Pending),
            CreatedAt = item.CreatedAt,
            CreatedBy = item.CreatedBy,
            UpdatedAt = item.UpdatedAt,
            UpdatedBy = item.UpdatedBy,
            RowVersion = item.RowVersion,
        });

    /// <summary>Loads the current range rows of a page of items with one query (never one per item).</summary>
    public static async Task<IReadOnlyList<ItemRow>> WithCurrentAsync(ProjectsDbContext db, IReadOnlyList<ItemRow> items, CancellationToken cancellationToken)
    {
        var ids = items.Where(item => item.CurrentRangeId is not null).Select(item => item.CurrentRangeId!.Value).ToArray();
        if (ids.Length == 0)
        {
            return items;
        }

        var ranges = await Ranges(db.PriceRanges.AsNoTracking().Where(range => ids.Contains(range.Id))).ToDictionaryAsync(range => range.Id, cancellationToken);
        foreach (var item in items.Where(item => item.CurrentRangeId is not null))
        {
            item.Current = ranges.GetValueOrDefault(item.CurrentRangeId!.Value);
        }

        return items;
    }

    /// <summary>One item with its history, or <c>null</c>.</summary>
    public static async Task<PriceItemDetail?> DetailAsync(ProjectsDbContext db, IUserDirectory users, int priceItemId, DateOnly today, CancellationToken cancellationToken)
    {
        var item = await Items(db, db.PriceItems.AsNoTracking().Where(candidate => candidate.Id == priceItemId), today).FirstOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return null;
        }

        await WithCurrentAsync(db, [item], cancellationToken);

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
