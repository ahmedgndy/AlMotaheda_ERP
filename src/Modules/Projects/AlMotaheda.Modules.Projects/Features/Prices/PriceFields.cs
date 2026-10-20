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

/// <summary>The range fields of a form, as sent. <c>Kind</c> defaults to <c>Range</c>.</summary>
internal sealed record RangeInput(
    int? SupplierId,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? EffectiveFrom,
    string? EffectiveTo,
    string? Conditions,
    string? Currency,
    string? Kind = null,
    decimal? ReferencePrice = null,
    decimal? TolerancePercent = null,
    string? SourceNote = null);

/// <summary>A price range as sent: the item's first range on create, or a change request.</summary>
/// <param name="MinPrice">The bounds compared with, computed for a fixed price from the reference and the tolerance.</param>
internal sealed record RangeFields(
    int? SupplierId,
    decimal MinPrice,
    decimal MaxPrice,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string? Conditions,
    PriceRuleKind Kind = PriceRuleKind.Range,
    decimal? ReferencePrice = null,
    decimal? TolerancePercent = null,
    string? SourceNote = null)
{
    /// <param name="defaultFrom">The start date when none is sent: today.</param>
    public static RangeFields? Validate(RangeInput input, DateOnly defaultFrom, ValidationErrors errors)
    {
        var (supplierId, minPrice, maxPrice, effectiveFrom, effectiveTo, conditions, currency, _, _, _, _) = input;
        if (supplierId is < 1)
        {
            errors.Add("supplierId", "supplierId is not valid.");
        }

        var kind = Input.Clean(input.Kind) is null ? PriceRuleKind.Range : Input.RequiredName<PriceRuleKind>(input.Kind, errors, "kind") ?? PriceRuleKind.Range;
        var reference = NumberInput.OptionalMoney(input.ReferencePrice, errors, "referencePrice", "The reference price");
        if (input.TolerancePercent is { } tolerance && (tolerance < 0 || tolerance > 100))
        {
            errors.Add("tolerancePercent", "tolerancePercent must be between 0 and 100.");
        }

        decimal? min, max;
        if (kind == PriceRuleKind.Fixed)
        {
            // A fixed price: the reference is required and the bounds come from it and the tolerance.
            if (reference is null)
            {
                errors.Add("referencePrice", "A fixed price needs its reference price.");
                min = max = null;
            }
            else
            {
                (min, max) = PriceRule.Bounds(kind, null, null, reference, input.TolerancePercent);
            }
        }
        else
        {
            min = NumberInput.RequiredMoney(minPrice, errors, "minPrice", "The lowest price");
            max = NumberInput.RequiredMoney(maxPrice, errors, "maxPrice", "The highest price");
            if (min is { } low && max is { } high && high < low)
            {
                errors.Add("maxPrice", "The highest price can't be below the lowest price.");
            }

            if (reference is { } mid && min is { } lo && max is { } hi && (mid < lo || mid > hi))
            {
                errors.Add("referencePrice", "The reference price must be between the lowest and the highest price.");
            }

            if (input.TolerancePercent is not null)
            {
                errors.Add("tolerancePercent", "A tolerance applies to a fixed price only; a range has its own bounds.");
            }
        }

        var sourceNote = Input.OptionalText(input.SourceNote, PriceRangeConfiguration.TextMaxLength, errors, "sourceNote", "The source");

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

        return errors.HasErrors ? null : new RangeFields(supplierId, min!.Value, max!.Value, from, to, text, kind, reference, kind == PriceRuleKind.Fixed ? input.TolerancePercent : null, sourceNote);
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
        Kind = Kind,
        ReferencePrice = ReferencePrice,
        TolerancePercent = TolerancePercent,
        SourceNote = SourceNote,
        Status = status,
        Reason = reason,
        RequestedAt = now,
        RequestedBy = userId,
        DecidedAt = status == PriceRangeStatus.Pending ? null : now,
        DecidedBy = status == PriceRangeStatus.Pending ? null : userId,
    };
}

/// <summary>The price item form (names, category, notes). The unit is set once, on create.</summary>
/// <param name="CategoryId">Resolved by <see cref="ResolveCategoryAsync"/> from the category code sent.</param>
internal sealed record PriceItemFields(string NameAr, string NameEn, string CategoryCode, string? Notes, int CategoryId = 0)
{
    /// <param name="classification">The category code; the field keeps its old name on the API (<c>classification</c>), <c>categoryCode</c> is accepted too.</param>
    public static PriceItemFields? Validate(string? nameAr, string? nameEn, string? classification, string? notes, ValidationErrors errors)
    {
        var ar = Input.RequiredText(nameAr, PriceItemConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var en = Input.RequiredText(nameEn, PriceItemConfiguration.NameMaxLength, errors, "nameEn", "The English name");
        var code = Input.RequiredText(classification, PriceCategoryConfiguration.CodeMaxLength, errors, "classification", "The category");
        var text = Input.OptionalText(notes, PriceItemConfiguration.NotesMaxLength, errors, "notes", "Notes");
        return errors.HasErrors ? null : new PriceItemFields(ar!, en!, code!, text);
    }

    /// <summary>The category must exist and be active (a form error on <c>classification</c> otherwise).</summary>
    public async Task<PriceItemFields> ResolveCategoryAsync(ProjectsDbContext db, ValidationErrors errors, CancellationToken cancellationToken)
    {
        var category = await db.PriceCategories.AsNoTracking()
            .Where(candidate => candidate.Code == CategoryCode)
            .Select(candidate => new { candidate.Id, candidate.IsActive })
            .SingleOrDefaultAsync(cancellationToken);
        if (category is null)
        {
            errors.Add("classification", "This category is not in the price list's categories.");
            return this;
        }

        if (!category.IsActive)
        {
            errors.Add("classification", "This category is inactive.");
        }

        return this with { CategoryId = category.Id };
    }

    public void ApplyTo(PriceItem item)
    {
        item.NameAr = NameAr;
        item.NameEn = NameEn;
        item.CategoryId = CategoryId;
        item.Notes = Notes;
    }

    public Task<bool> NameTakenAsync(ProjectsDbContext db, int? exceptId, CancellationToken cancellationToken) =>
        db.PriceItems.AsNoTracking().AnyAsync(
            item => item.Id != exceptId && (item.NameEn == NameEn || item.NameAr == NameAr), cancellationToken);

    public static bool IsNameRace(DbUpdateException exception) =>
        SqlViolations.IsUnique(exception, PriceItemConfiguration.NameEnIndex)
        || SqlViolations.IsUnique(exception, PriceItemConfiguration.NameArIndex);

    /// <summary>The unit must be an active price unit (the configurable list), matched by code, case-insensitively.</summary>
    public static async Task<string?> UnitAsync(ProjectsDbContext db, string? unit, ValidationErrors errors, CancellationToken cancellationToken)
    {
        var code = Input.Clean(unit);
        if (code is null)
        {
            errors.Add("unit", "unit is required.");
            return null;
        }

        var found = await db.PriceUnits.AsNoTracking()
            .Where(candidate => candidate.Code == code)
            .Select(candidate => new { candidate.Code, candidate.IsActive })
            .FirstOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            errors.Add("unit", "This unit is not in the price list's units.");
            return null;
        }

        if (!found.IsActive)
        {
            errors.Add("unit", "This unit is inactive.");
            return null;
        }

        return found.Code;
    }
}

/// <summary>Changes to one price item's ranges run one at a time (company-wide, not per project).</summary>
internal static class PriceItemLock
{
    public static Task<bool> TryAcquireAsync(ProjectsDbContext db, int priceItemId, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, $"AlMotaheda.Projects.PriceItem.{priceItemId}", cancellationToken);
}
