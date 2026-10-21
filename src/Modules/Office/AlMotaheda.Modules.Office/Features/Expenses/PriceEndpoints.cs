using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Features.Expenses;

/// <summary>
/// The office's view of the central Price Control catalog: <c>GET /api/office/price-items</c> lists the office
/// categories' items with today's range (the app keeps a copy for offline hints), and <c>POST /api/office/price-check</c>
/// compares a price before saving. The server compares again when the expense is saved, whatever the app showed.
/// </summary>
internal static class OfficePriceEndpoints
{
    internal sealed record PriceItemView(
        int Id, string NameAr, string NameEn, string Unit, string CategoryCode, int? RangeId, decimal? MinPrice, decimal? MaxPrice, decimal? ReferencePrice);

    internal sealed record PriceItemsResponse(DateOnly Date, IReadOnlyList<PriceItemView> Items);

    internal sealed record CheckRequest(int? PriceItemId, string? Unit, decimal? UnitPrice, decimal? Quantity, string? Date);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/office/price-items", ListAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("ListOfficePriceItems")
            .WithSummary("The office categories of the price list, with the range in force today");

        api.MapPost("/office/price-check", CheckAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("CheckOfficePrice")
            .WithSummary("Compare a unit price with the central price list (nothing is saved)");
    }

    internal static async Task<Ok<PriceItemsResponse>> ListAsync(
        IPriceControl priceControl, IOptions<OfficeOptions> settings, CompanyClock clock, CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var items = await priceControl.ListItemsAsync(settings.Value.PriceCategories, today, cancellationToken);
        return TypedResults.Ok(new PriceItemsResponse(today, items
            .Select(listing => new PriceItemView(listing.Item.Id, listing.Item.NameAr, listing.Item.NameEn, listing.Item.Unit, listing.Item.CategoryCode,
                listing.RangeId, listing.MinPrice, listing.MaxPrice, listing.ReferencePrice))
            .ToList()));
    }

    internal static async Task<Results<Ok<PriceVerdict>, ValidationProblem>> CheckAsync(
        CheckRequest request, IPriceControl priceControl, IOptions<OfficeOptions> settings, CompanyClock clock, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        if (request.PriceItemId is null)
        {
            errors.Add("priceItemId", "Choose a price item.");
        }

        var unit = Input.RequiredText(request.Unit, Data.Configurations.OfficeLengths.Unit, errors, "unit", "The unit");
        var unitPrice = NumberInput.RequiredMoney(request.UnitPrice, errors, "unitPrice", "The unit price");
        var quantity = request.Quantity is null ? 1m : NumberInput.RequiredQuantity(request.Quantity, errors, "quantity", "The quantity");
        var date = Input.OptionalDate(request.Date, errors, "date", "The date") ?? clock.Today;
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var items = await priceControl.FindItemsAsync([request.PriceItemId!.Value], cancellationToken);
        if (!items.TryGetValue(request.PriceItemId.Value, out var item) || !settings.Value.PriceCategories.Contains(item.CategoryCode))
        {
            errors.Add("priceItemId", "Choose an item of the office price list.");
            return OfficeProblems.Validation(errors);
        }

        return TypedResults.Ok(await priceControl.CheckAsync(
            new PriceCheckRequest(item.Id, null, date, unit!, unitPrice!.Value, quantity!.Value), cancellationToken));
    }
}
