using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Catalog;

/// <summary>The catalog item form, as sent on create and on edit.</summary>
internal interface ICatalogItemRequest
{
    string? NameAr { get; }
    string? NameEn { get; }
    string? Category { get; }
    string? Unit { get; }
    decimal? EstimatedQuantity { get; }
    int? SupplierId { get; }
}

/// <summary>The catalog item form after validation. The price is never part of it: prices change by request only.</summary>
internal sealed record CatalogItemFields(
    string NameAr,
    string NameEn,
    CatalogCategory Category,
    string Unit,
    decimal EstimatedQuantity,
    int? SupplierId)
{
    public static CatalogItemFields? Validate(ICatalogItemRequest request, ValidationErrors errors)
    {
        var nameAr = Input.RequiredText(request.NameAr, CatalogItemConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, CatalogItemConfiguration.NameMaxLength, errors, "nameEn", "The English name");

        if (!Input.TryParseName<CatalogCategory>(request.Category, out var category))
        {
            errors.Add("category", $"category must be one of: {string.Join(", ", Enum.GetNames<CatalogCategory>())}.");
        }

        if (!MeasurementUnits.TryParse(request.Unit, out var unit))
        {
            errors.Add("unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
        }

        var estimated = request.EstimatedQuantity ?? 0;
        if (estimated < 0 || estimated > NumberInput.MaxQuantity || decimal.Round(estimated, 3) != estimated)
        {
            errors.Add("estimatedQuantity", $"The estimated quantity must be zero or more, with at most three decimals, and at most {NumberInput.MaxQuantity:0}.");
        }

        if (request.SupplierId is < 1)
        {
            errors.Add("supplierId", "supplierId is not valid.");
        }

        return errors.HasErrors ? null : new CatalogItemFields(nameAr!, nameEn!, category, unit, estimated, request.SupplierId);
    }

    public void ApplyTo(CatalogItem item)
    {
        item.NameAr = NameAr;
        item.NameEn = NameEn;
        item.Category = Category;
        item.Unit = Unit;
        item.EstimatedQuantity = EstimatedQuantity;
        item.SupplierId = SupplierId;
    }

    /// <summary>
    /// The usual supplier must exist. An inactive supplier can't be chosen, but an item that already names one keeps it.
    /// </summary>
    public async Task CheckSupplierAsync(ProjectsDbContext db, int? currentSupplierId, ValidationErrors errors, CancellationToken cancellationToken)
    {
        if (SupplierId is not { } supplierId || supplierId == currentSupplierId)
        {
            return;
        }

        var status = await db.Suppliers.AsNoTracking()
            .Where(supplier => supplier.Id == supplierId)
            .Select(supplier => (SupplierStatus?)supplier.Status)
            .SingleOrDefaultAsync(cancellationToken);

        if (status is null)
        {
            errors.Add("supplierId", "This supplier was not found.");
        }
        else if (status == SupplierStatus.Inactive)
        {
            errors.Add("supplierId", "This supplier is inactive and can't be chosen.");
        }
    }

    /// <summary>True when another catalog item of the project already has either name.</summary>
    public Task<bool> NameTakenAsync(ProjectsDbContext db, int projectId, int? exceptItemId, CancellationToken cancellationToken) =>
        db.CatalogItems.AsNoTracking().AnyAsync(
            item => item.ProjectId == projectId && item.Id != exceptItemId && (item.NameEn == NameEn || item.NameAr == NameAr),
            cancellationToken);

    /// <summary>True when the save failed on one of the catalog name indexes.</summary>
    public static bool IsNameRace(DbUpdateException exception) =>
        SqlViolations.IsUnique(exception, CatalogItemConfiguration.NameIndex)
        || SqlViolations.IsUnique(exception, CatalogItemConfiguration.NameArIndex);
}
