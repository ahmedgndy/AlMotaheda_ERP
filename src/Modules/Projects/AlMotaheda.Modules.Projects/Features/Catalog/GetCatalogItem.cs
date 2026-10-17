using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Catalog;

/// <summary>
/// <c>GET /api/projects/{pid}/catalog-items/{id}</c>: one catalog item, every price it ever had (nothing is
/// overwritten), newest start date first, and its last 10 supplies.
/// </summary>
internal static class GetCatalogItem
{
    public const int RecentSupplyCount = 10;

    internal sealed record Response(CatalogItemView Item, IReadOnlyList<PriceChangeView> History, IReadOnlyList<CatalogSupplyView> RecentSupplies);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/catalog-items/{itemId:int}", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("GetCatalogItem")
            .WithSummary("Get one catalog item with its full price history and its last 10 supplies");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int itemId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        CancellationToken cancellationToken)
    {
        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var response = await LoadAsync(db, users, projectId, itemId, clock.Today,
            httpContext.User.HasPermission(CatalogPermissions.Module, PermissionAction.SeeMoney), cancellationToken);

        return response is null ? PlanningProblems.CatalogItemNotFound() : TypedResults.Ok(response);
    }

    /// <summary>Reads one item of the project with its history and supplies, or <c>null</c> when it is not on this project.</summary>
    internal static async Task<Response?> LoadAsync(
        ProjectsDbContext db, IUserDirectory users, int projectId, int itemId, DateOnly today, bool seesMoney, CancellationToken cancellationToken)
    {
        var header = await CatalogQueries.One(db, projectId, itemId).SingleOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var prices = await CatalogQueries.Prices(db, projectId, itemId).ToListAsync(cancellationToken);
        var masterPrices = await CatalogQueries.MasterPrices(db, projectId, itemId).ToListAsync(cancellationToken);
        var purchases = await CatalogQueries.Purchases(db, projectId, itemId).ToListAsync(cancellationToken);
        var supplies = await CatalogQueries.RecentSupplies(db, projectId, itemId, RecentSupplyCount).ToListAsync(cancellationToken);
        var people = await PlanningPeople.FindAsync(users, PriceChangeView.People(prices), cancellationToken);

        var history = prices
            .OrderByDescending(price => price.EffectiveFrom)
            .ThenByDescending(price => price.Id)
            .Select(price => PriceChangeView.From(price, header.NameAr, header.NameEn, people))
            .ToList();

        return new Response(
            CatalogItemView.From(header, prices, masterPrices, purchases, today, seesMoney, people),
            history,
            supplies.Select(supply => CatalogSupplyView.From(supply, seesMoney)).ToList());
    }
}
