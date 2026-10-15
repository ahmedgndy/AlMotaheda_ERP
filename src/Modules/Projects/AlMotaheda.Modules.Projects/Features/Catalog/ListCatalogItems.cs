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
/// <c>GET /api/projects/{pid}/catalog-items</c>: one page of the project's price catalog, each item with where
/// its price stands today, and the summary of the whole catalog at the top.
/// </summary>
/// <remarks>
/// Filters and paging run in SQL. The prices and purchases of the project are then loaded flat, once, and
/// <see cref="CatalogPricing"/> works out each row and the summary from the same rows.
/// </remarks>
internal static class ListCatalogItems
{
    internal sealed record Response(IReadOnlyList<CatalogItemView> Items, int Page, int PageSize, int TotalCount, CatalogSummary Summary);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/catalog-items", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("ListCatalogItems")
            .WithSummary("List a project's price catalog (filters: category, status, priceStatus, search) with current, previous, pending and scheduled prices");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        CancellationToken cancellationToken,
        string? category = null,
        string? status = null,
        string? priceStatus = null,
        string? search = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);

        CatalogCategory? categoryFilter = null;
        if (Input.Clean(category) is { } categoryText)
        {
            if (Input.TryParseName<CatalogCategory>(categoryText, out var parsed))
            {
                categoryFilter = parsed;
            }
            else
            {
                errors.Add("category", $"category must be one of: {string.Join(", ", Enum.GetNames<CatalogCategory>())}.");
            }
        }

        CatalogItemStatus? statusFilter = null;
        if (Input.Clean(status) is { } statusText)
        {
            if (Input.TryParseName<CatalogItemStatus>(statusText, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add("status", "status must be Active or Inactive.");
            }
        }

        string? priceStatusFilter = null;
        if (Input.Clean(priceStatus) is { } priceStatusText)
        {
            priceStatusFilter = CatalogPriceStatuses.All.FirstOrDefault(name => string.Equals(name, priceStatusText, StringComparison.OrdinalIgnoreCase));
            if (priceStatusFilter is null)
            {
                errors.Add("priceStatus", $"priceStatus must be one of: {string.Join(", ", CatalogPriceStatuses.All)}.");
            }
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var today = clock.Today;
        var seesMoney = httpContext.User.HasPermission(CatalogPermissions.Module, PermissionAction.SeeMoney);

        var query = CatalogQueries.Filtered(db, projectId, new CatalogFilter(categoryFilter, statusFilter, priceStatusFilter, Input.Clean(search)), today);
        var totalCount = await query.CountAsync(cancellationToken);
        var headers = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        var prices = await CatalogQueries.Prices(db, projectId).ToListAsync(cancellationToken);
        var purchases = await CatalogQueries.Purchases(db, projectId).ToListAsync(cancellationToken);
        var estimates = await CatalogQueries.Estimates(db, projectId).ToListAsync(cancellationToken);

        var pageIds = headers.Select(header => header.Id).ToHashSet();
        var people = await PlanningPeople.FindAsync(
            users,
            PriceChangeView.People(prices.Where(price => pageIds.Contains(price.CatalogItemId) && price.Status == ItemPriceStatus.Pending)),
            cancellationToken);

        var items = CatalogItemView.FromRows(headers, prices, purchases, today, seesMoney, people);
        var summary = CatalogSummary.Compute(estimates, prices, purchases, today, seesMoney);

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount, summary));
    }
}
