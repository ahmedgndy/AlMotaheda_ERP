using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Catalog;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Prices;

/// <summary>The price list: permission module <c>catalog</c>, like the project catalog it replaces in the screens.</summary>
internal static class PricePermissions
{
    public const string Module = CatalogPermissions.Module;
}

/// <summary>A price range as sent: the item's first range on create, or a change request.</summary>
internal sealed record RangeFields(int? SupplierId, decimal MinPrice, decimal MaxPrice, DateOnly EffectiveFrom, DateOnly? EffectiveTo, string? Conditions)
{
    /// <param name="defaultFrom">The start date when none is sent: today.</param>
    public static RangeFields? Validate(
        int? supplierId, decimal? minPrice, decimal? maxPrice, string? effectiveFrom, string? effectiveTo, string? conditions,
        string? currency, DateOnly defaultFrom, ValidationErrors errors)
    {
        if (supplierId is < 1)
        {
            errors.Add("supplierId", "supplierId is not valid.");
        }

        var min = NumberInput.RequiredMoney(minPrice, errors, "minPrice", "The lowest price");
        var max = NumberInput.RequiredMoney(maxPrice, errors, "maxPrice", "The highest price");
        if (min is { } low && max is { } high && high < low)
        {
            errors.Add("maxPrice", "The highest price can't be below the lowest price.");
        }

        if (Input.Clean(currency) is { } code && !string.Equals(code, PriceCurrencies.Egp, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("currency", "Only EGP is supported.");
        }

        var from = Input.OptionalDate(effectiveFrom, errors, "effectiveFrom", "The effective date") ?? defaultFrom;
        var to = Input.OptionalDate(effectiveTo, errors, "effectiveTo", "The expiry date");
        if (to is { } end && end < from)
        {
            errors.Add("effectiveTo", "The expiry date can't be before the effective date.");
        }

        var text = Input.OptionalText(conditions, PriceRangeConfiguration.TextMaxLength, errors, "conditions", "The conditions");

        return errors.HasErrors ? null : new RangeFields(supplierId, min!.Value, max!.Value, from, to, text);
    }

    /// <summary>A supplier-specific range needs a supplier that exists and is active.</summary>
    public async Task CheckSupplierAsync(ProjectsDbContext db, ValidationErrors errors, CancellationToken cancellationToken)
    {
        if (SupplierId is not { } id)
        {
            return;
        }

        var status = await db.Suppliers.AsNoTracking().Where(supplier => supplier.Id == id)
            .Select(supplier => (SupplierStatus?)supplier.Status).SingleOrDefaultAsync(cancellationToken);
        if (status is null)
        {
            errors.Add("supplierId", "This supplier was not found.");
        }
        else if (status != SupplierStatus.Active)
        {
            errors.Add("supplierId", "This supplier is inactive and can't be chosen.");
        }
    }

    public PriceRange ToRange(PriceRangeStatus status, int userId, DateTime now, string? reason) => new()
    {
        SupplierId = SupplierId,
        MinPrice = MinPrice,
        MaxPrice = MaxPrice,
        Currency = PriceCurrencies.Egp,
        EffectiveFrom = EffectiveFrom,
        EffectiveTo = EffectiveTo,
        Conditions = Conditions,
        Status = status,
        Reason = reason,
        RequestedAt = now,
        RequestedBy = userId,
        DecidedAt = status == PriceRangeStatus.Pending ? null : now,
        DecidedBy = status == PriceRangeStatus.Pending ? null : userId,
    };
}

/// <summary>The price item form (names, classification, notes). The unit is set once, on create.</summary>
internal sealed record PriceItemFields(string NameAr, string NameEn, CatalogCategory Classification, string? Notes)
{
    public static PriceItemFields? Validate(string? nameAr, string? nameEn, string? classification, string? notes, ValidationErrors errors)
    {
        var ar = Input.RequiredText(nameAr, PriceItemConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var en = Input.RequiredText(nameEn, PriceItemConfiguration.NameMaxLength, errors, "nameEn", "The English name");
        if (!Input.TryParseName<CatalogCategory>(classification, out var parsed))
        {
            errors.Add("classification", $"classification must be one of: {string.Join(", ", Enum.GetNames<CatalogCategory>())}.");
        }

        var text = Input.OptionalText(notes, PriceItemConfiguration.NotesMaxLength, errors, "notes", "Notes");
        return errors.HasErrors ? null : new PriceItemFields(ar!, en!, parsed, text);
    }

    public void ApplyTo(PriceItem item)
    {
        item.NameAr = NameAr;
        item.NameEn = NameEn;
        item.Classification = Classification;
        item.Notes = Notes;
    }

    public Task<bool> NameTakenAsync(ProjectsDbContext db, int? exceptId, CancellationToken cancellationToken) =>
        db.PriceItems.AsNoTracking().AnyAsync(
            item => item.Id != exceptId && (item.NameEn == NameEn || item.NameAr == NameAr), cancellationToken);

    public static bool IsNameRace(DbUpdateException exception) =>
        SqlViolations.IsUnique(exception, PriceItemConfiguration.NameEnIndex)
        || SqlViolations.IsUnique(exception, PriceItemConfiguration.NameArIndex);

    public static bool TryUnit(string? unit, ValidationErrors errors, out string parsed)
    {
        if (MeasurementUnits.TryParse(unit, out parsed))
        {
            return true;
        }

        errors.Add("unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
        return false;
    }
}

/// <summary>Changes to one price item's ranges run one at a time (company-wide, not per project).</summary>
internal static class PriceItemLock
{
    public static Task<bool> TryAcquireAsync(ProjectsDbContext db, int priceItemId, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, $"AlMotaheda.Projects.PriceItem.{priceItemId}", cancellationToken);
}
