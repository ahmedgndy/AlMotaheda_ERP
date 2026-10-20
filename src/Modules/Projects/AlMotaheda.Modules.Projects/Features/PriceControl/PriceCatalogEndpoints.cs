using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Prices;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.PriceControl;

/// <summary>
/// The configurable lists of Price Control: categories (<c>/api/price-categories</c>) and units
/// (<c>/api/price-units</c>). Read by anyone who records purchases or sees the price list; changed with catalog
/// Create, like adding a price item. A code is fixed once created (records store it); names and status change.
/// </summary>
internal static class PriceCatalogEndpoints
{
    internal sealed record Request(string? Code, string? NameAr, string? NameEn, bool? IsActive, int? SortOrder, string? RowVersion);

    internal sealed record CategoriesResponse(IReadOnlyList<PriceCategoryView> Items);

    internal sealed record UnitsResponse(IReadOnlyList<PriceUnitView> Items);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/price-categories", ListCategoriesAsync)
            .RequireAnyPermission((PricePermissions.Module, PermissionAction.View), (ProjectPermissions.Module, PermissionAction.Create), (ProjectPermissions.ProcurementModule, PermissionAction.View))
            .WithName("ListPriceCategories")
            .WithSummary("The categories of the price list (includeInactive)");

        api.MapPost("/price-categories", CreateCategoryAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Create)
            .WithName("CreatePriceCategory")
            .WithSummary("Add a category to the price list");

        api.MapPut("/price-categories/{categoryId:int}", UpdateCategoryAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Create)
            .WithName("UpdatePriceCategory")
            .WithSummary("Rename, reorder or deactivate a category (its code is fixed)");

        api.MapGet("/price-units", ListUnitsAsync)
            .RequireAnyPermission((PricePermissions.Module, PermissionAction.View), (ProjectPermissions.Module, PermissionAction.Create), (ProjectPermissions.ProcurementModule, PermissionAction.View))
            .WithName("ListPriceUnits")
            .WithSummary("The units prices are quoted per (includeInactive)");

        api.MapPost("/price-units", CreateUnitAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Create)
            .WithName("CreatePriceUnit")
            .WithSummary("Add a unit to the price list");

        api.MapPut("/price-units/{unitId:int}", UpdateUnitAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Create)
            .WithName("UpdatePriceUnit")
            .WithSummary("Rename, reorder or deactivate a unit (its code is fixed)");
    }

    internal static async Task<Ok<CategoriesResponse>> ListCategoriesAsync(ProjectsDbContext db, CancellationToken cancellationToken, bool includeInactive = false)
    {
        var rows = await db.PriceCategories.AsNoTracking()
            .Where(category => includeInactive || category.IsActive)
            .OrderBy(category => category.SortOrder).ThenBy(category => category.Id)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new CategoriesResponse(rows.Select(PriceCategoryView.From).ToList()));
    }

    internal static async Task<Ok<UnitsResponse>> ListUnitsAsync(ProjectsDbContext db, CancellationToken cancellationToken, bool includeInactive = false)
    {
        var rows = await db.PriceUnits.AsNoTracking()
            .Where(unit => includeInactive || unit.IsActive)
            .OrderBy(unit => unit.SortOrder).ThenBy(unit => unit.Id)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new UnitsResponse(rows.Select(PriceUnitView.From).ToList()));
    }

    internal static async Task<Results<Created<PriceCategoryView>, ValidationProblem, ProblemHttpResult>> CreateCategoryAsync(
        Request request, HttpContext httpContext, ProjectsDbContext db, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var code = Code(request.Code, PriceCategoryConfiguration.CodeMaxLength, errors);
        var (nameAr, nameEn) = Names(request, PriceCategoryConfiguration.NameMaxLength, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await db.PriceCategories.AnyAsync(category => category.Code == code || category.NameAr == nameAr || category.NameEn == nameEn, cancellationToken))
        {
            return PriceControlProblems.CategoryCodeTaken();
        }

        var category = new PriceCategory
        {
            Code = code!, NameAr = nameAr!, NameEn = nameEn!, IsActive = request.IsActive ?? true,
            SortOrder = request.SortOrder ?? (await db.PriceCategories.MaxAsync(other => (int?)other.SortOrder, cancellationToken) ?? 0) + 1,
            UpdatedAt = clock.UtcNow, UpdatedBy = userId,
        };
        db.PriceCategories.Add(category);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PriceCategoryConfiguration.CodeIndex))
        {
            return PriceControlProblems.CategoryCodeTaken();
        }

        log.PriceCategorySaved(userId, category.Id, category.Code);
        return TypedResults.Created($"/api/price-categories/{category.Id}", PriceCategoryView.From(category));
    }

    internal static async Task<Results<Ok<PriceCategoryView>, ValidationProblem, ProblemHttpResult>> UpdateCategoryAsync(
        int categoryId, Request request, HttpContext httpContext, ProjectsDbContext db, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (nameAr, nameEn) = Names(request, PriceCategoryConfiguration.NameMaxLength, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var category = await db.PriceCategories.SingleOrDefaultAsync(candidate => candidate.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return PriceControlProblems.CategoryNotFound();
        }

        if (await db.PriceCategories.AnyAsync(other => other.Id != categoryId && (other.NameAr == nameAr || other.NameEn == nameEn), cancellationToken))
        {
            return PriceControlProblems.CategoryCodeTaken();
        }

        category.NameAr = nameAr!;
        category.NameEn = nameEn!;
        category.IsActive = request.IsActive ?? category.IsActive;
        category.SortOrder = request.SortOrder ?? category.SortOrder;
        category.UpdatedAt = clock.UtcNow;
        category.UpdatedBy = userId;
        db.Entry(category).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.PriceCategorySaved(userId, category.Id, category.Code);
        return TypedResults.Ok(PriceCategoryView.From(category));
    }

    internal static async Task<Results<Created<PriceUnitView>, ValidationProblem, ProblemHttpResult>> CreateUnitAsync(
        Request request, HttpContext httpContext, ProjectsDbContext db, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var code = Code(request.Code, PriceUnitConfiguration.CodeMaxLength, errors);
        var (nameAr, nameEn) = Names(request, PriceUnitConfiguration.NameMaxLength, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await db.PriceUnits.AnyAsync(unit => unit.Code == code || unit.NameAr == nameAr || unit.NameEn == nameEn, cancellationToken))
        {
            return PriceControlProblems.UnitCodeTaken();
        }

        var unit = new PriceUnit
        {
            Code = code!, NameAr = nameAr!, NameEn = nameEn!, IsActive = request.IsActive ?? true,
            SortOrder = request.SortOrder ?? (await db.PriceUnits.MaxAsync(other => (int?)other.SortOrder, cancellationToken) ?? 0) + 1,
            UpdatedAt = clock.UtcNow, UpdatedBy = userId,
        };
        db.PriceUnits.Add(unit);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PriceUnitConfiguration.CodeIndex))
        {
            return PriceControlProblems.UnitCodeTaken();
        }

        log.PriceUnitSaved(userId, unit.Id, unit.Code);
        return TypedResults.Created($"/api/price-units/{unit.Id}", PriceUnitView.From(unit));
    }

    internal static async Task<Results<Ok<PriceUnitView>, ValidationProblem, ProblemHttpResult>> UpdateUnitAsync(
        int unitId, Request request, HttpContext httpContext, ProjectsDbContext db, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (nameAr, nameEn) = Names(request, PriceUnitConfiguration.NameMaxLength, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var unit = await db.PriceUnits.SingleOrDefaultAsync(candidate => candidate.Id == unitId, cancellationToken);
        if (unit is null)
        {
            return PriceControlProblems.UnitNotFound();
        }

        if (await db.PriceUnits.AnyAsync(other => other.Id != unitId && (other.NameAr == nameAr || other.NameEn == nameEn), cancellationToken))
        {
            return PriceControlProblems.UnitCodeTaken();
        }

        unit.NameAr = nameAr!;
        unit.NameEn = nameEn!;
        unit.IsActive = request.IsActive ?? unit.IsActive;
        unit.SortOrder = request.SortOrder ?? unit.SortOrder;
        unit.UpdatedAt = clock.UtcNow;
        unit.UpdatedBy = userId;
        db.Entry(unit).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.PriceUnitSaved(userId, unit.Id, unit.Code);
        return TypedResults.Ok(PriceUnitView.From(unit));
    }

    /// <summary>A code: Latin letters and digits, no spaces, so the client can key its labels on it.</summary>
    private static string? Code(string? code, int maxLength, ValidationErrors errors)
    {
        var clean = Input.RequiredText(code, maxLength, errors, "code", "The code");
        if (clean is not null && !clean.All(char.IsAsciiLetterOrDigit))
        {
            errors.Add("code", "The code uses Latin letters and digits only, with no spaces.");
            return null;
        }

        return clean;
    }

    private static (string? NameAr, string? NameEn) Names(Request request, int maxLength, ValidationErrors errors) => (
        Input.RequiredText(request.NameAr, maxLength, errors, "nameAr", "The Arabic name"),
        Input.RequiredText(request.NameEn, maxLength, errors, "nameEn", "The English name"));
}
