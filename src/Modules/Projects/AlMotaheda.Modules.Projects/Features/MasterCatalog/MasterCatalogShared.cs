using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Features.Projects;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.MasterCatalog;

/// <summary>The master item form, as sent on create and on edit. The price is never part of an edit.</summary>
internal interface IMasterItemRequest
{
    string? NameAr { get; }
    string? NameEn { get; }
    string? Kind { get; }
    string? Category { get; }
    string? Unit { get; }
    string? Description { get; }
}

/// <summary>The master item form after validation.</summary>
internal sealed record MasterItemFields(string NameAr, string NameEn, MasterItemKind Kind, string Category, string Unit, string? Description)
{
    /// <summary>The categories a kind accepts: a work type for work items, a catalog category for the others.</summary>
    public static IReadOnlyList<string> CategoriesOf(MasterItemKind kind) =>
        MasterItem.IsWork(kind) ? WorkTypes.All : Enum.GetNames<CatalogCategory>();

    public static MasterItemFields? Validate(IMasterItemRequest request, ValidationErrors errors)
    {
        var nameAr = Input.RequiredText(request.NameAr, MasterItemConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, MasterItemConfiguration.NameMaxLength, errors, "nameEn", "The English name");

        var hasKind = Input.TryParseName<MasterItemKind>(request.Kind, out var kind);
        if (!hasKind)
        {
            errors.Add("kind", $"kind must be one of: {string.Join(", ", Enum.GetNames<MasterItemKind>())}.");
        }

        string? category = null;
        if (hasKind)
        {
            var allowed = CategoriesOf(kind);
            category = allowed.FirstOrDefault(name => string.Equals(name, Input.Clean(request.Category), StringComparison.OrdinalIgnoreCase));
            if (category is null)
            {
                errors.Add("category", $"For {kind} items, category must be one of: {string.Join(", ", allowed)}.");
            }
        }

        if (!MeasurementUnits.TryParse(request.Unit, out var unit))
        {
            errors.Add("unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
        }

        var description = Input.OptionalText(request.Description, MasterItemConfiguration.DescriptionMaxLength, errors, "description", "The description");

        return errors.HasErrors ? null : new MasterItemFields(nameAr!, nameEn!, kind, category!, unit, description);
    }

    public Task<bool> NameTakenAsync(ProjectsDbContext db, int? exceptId, CancellationToken cancellationToken) =>
        db.MasterItems.AsNoTracking().AnyAsync(item => item.Id != exceptId && (item.NameEn == NameEn || item.NameAr == NameAr), cancellationToken);

    public static bool IsNameRace(DbUpdateException exception) =>
        SqlViolations.IsUnique(exception, MasterItemConfiguration.NameEnIndex)
        || SqlViolations.IsUnique(exception, MasterItemConfiguration.NameArIndex);
}

/// <summary>One master price row: a first price, a change request, or a decided change.</summary>
internal sealed record MasterPriceChangeView(
    int Id,
    int MasterItemId,
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
    /// <param name="row">A master price row; its <see cref="PriceRow.CatalogItemId"/> holds the master item id.</param>
    public static MasterPriceChangeView From(PriceRow row, string? itemNameAr, string? itemNameEn, IReadOnlyDictionary<int, UserSummary> people) =>
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

    public static PriceRow Row(MasterItemPrice price) => new(
        price.Id,
        price.MasterItemId,
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

/// <summary>One master item without prices, as the queries read it.</summary>
internal sealed record MasterItemHeader(
    int Id,
    string NameAr,
    string NameEn,
    MasterItemKind Kind,
    string Category,
    string Unit,
    string? Description,
    MasterItemStatus Status,
    int ProjectCount,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    byte[] RowVersion);

/// <summary>
/// One master item with where its controlled price stands today. Prices are shown to everyone with catalog View,
/// like the project catalog.
/// </summary>
/// <param name="Target">What selecting it creates on a project: <c>ProjectItem</c> or <c>CatalogItem</c>.</param>
/// <param name="ProjectCount">How many projects selected it.</param>
internal sealed record MasterItemView(
    int Id,
    string NameAr,
    string NameEn,
    string Kind,
    string Target,
    string Category,
    string Unit,
    string? Description,
    string Status,
    decimal? CurrentPrice,
    DateOnly? CurrentPriceFrom,
    decimal? PreviousPrice,
    decimal? ChangePercent,
    string PriceStatus,
    MasterPriceChangeView? PendingChange,
    ScheduledPriceView? ScheduledPrice,
    int ProjectCount,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public const string ProjectItemTarget = "ProjectItem";
    public const string CatalogItemTarget = "CatalogItem";

    public static MasterItemView From(MasterItemHeader header, IEnumerable<PriceRow> prices, DateOnly today, IReadOnlyDictionary<int, UserSummary> people)
    {
        var position = CatalogPricing.Position(prices, today);
        return new MasterItemView(
            header.Id,
            header.NameAr,
            header.NameEn,
            header.Kind.ToString(),
            MasterItem.IsWork(header.Kind) ? ProjectItemTarget : CatalogItemTarget,
            header.Category,
            header.Unit,
            header.Description,
            header.Status.ToString(),
            position.Current?.Price,
            position.Current?.EffectiveFrom,
            position.Previous?.Price,
            position.ChangePercent,
            position.PriceStatus,
            position.Pending is { } pending ? MasterPriceChangeView.From(pending, header.NameAr, header.NameEn, people) : null,
            position.Scheduled is { } scheduled ? new ScheduledPriceView(scheduled.Id, scheduled.Price, scheduled.EffectiveFrom) : null,
            header.ProjectCount,
            header.CreatedAt,
            header.CreatedBy,
            header.UpdatedAt,
            header.UpdatedBy,
            RowVersions.Format(header.RowVersion));
    }
}

/// <summary>The master catalog queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class MasterQueries
{
    public static IQueryable<MasterItemHeader> Filtered(
        ProjectsDbContext db, string? search, MasterItemKind? kind, string? category, MasterItemStatus? status)
    {
        var query = db.MasterItems.AsNoTracking();
        if (search is { } text)
        {
            query = query.Where(item => item.NameEn.Contains(text) || item.NameAr.Contains(text));
        }

        if (kind is { } wantedKind)
        {
            query = query.Where(item => item.Kind == wantedKind);
        }

        if (category is { } wantedCategory)
        {
            query = query.Where(item => item.Category == wantedCategory);
        }

        if (status is { } wantedStatus)
        {
            query = query.Where(item => item.Status == wantedStatus);
        }

        return Project(db, query.OrderBy(item => item.NameEn).ThenBy(item => item.Id));
    }

    public static IQueryable<MasterItemHeader> One(ProjectsDbContext db, int id) =>
        Project(db, db.MasterItems.AsNoTracking().Where(item => item.Id == id));

    /// <summary>The price rows of some master items (all when <paramref name="ids"/> is <c>null</c>), flat.</summary>
    public static IQueryable<PriceRow> Prices(ProjectsDbContext db, IReadOnlyCollection<int>? ids) =>
        db.MasterItemPrices.AsNoTracking()
            .Where(price => ids == null || ids.Contains(price.MasterItemId))
            .Select(price => new PriceRow(
                price.Id,
                price.MasterItemId,
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

    /// <summary>True when a project selected the item (as a catalog item or a project item).</summary>
    public static async Task<bool> InUseAsync(ProjectsDbContext db, int id, CancellationToken cancellationToken) =>
        await db.CatalogItems.AnyAsync(item => item.MasterItemId == id, cancellationToken)
        || await db.ProjectItems.AnyAsync(item => item.MasterItemId == id, cancellationToken);

    private static IQueryable<MasterItemHeader> Project(ProjectsDbContext db, IQueryable<MasterItem> items) =>
        items.Select(item => new MasterItemHeader(
            item.Id,
            item.NameAr,
            item.NameEn,
            item.Kind,
            item.Category,
            item.Unit,
            item.Description,
            item.Status,
            // A project selects a master item once, into one table (its kind decides which, and the kind is fixed
            // once used), so the two link counts add up to the number of projects.
            db.CatalogItems.Count(link => link.MasterItemId == item.Id) + db.ProjectItems.Count(link => link.MasterItemId == item.Id),
            item.CreatedAt,
            item.CreatedBy,
            item.UpdatedAt,
            item.UpdatedBy,
            item.RowVersion));
}
