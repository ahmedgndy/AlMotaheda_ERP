using AlMotaheda.BuildingBlocks.Authorization;
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
/// <c>GET /api/projects/{pid}/price-changes</c>: the project's price change requests, newest first; with
/// <c>status=Pending</c>, the approval queue. An item's first price is not a change and is not listed.
/// </summary>
internal static class ListPriceChanges
{
    internal sealed record Response(IReadOnlyList<PriceChangeView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/price-changes", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("ListPriceChanges")
            .WithSummary("List a project's price change requests, newest first (filter: status Pending, Approved or Rejected)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? status = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);

        ItemPriceStatus? statusFilter = null;
        if (Input.Clean(status) is { } statusText)
        {
            if (Input.TryParseName<ItemPriceStatus>(statusText, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add("status", "status must be Pending, Approved or Rejected.");
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

        var query = CatalogQueries.Changes(db, projectId, statusFilter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var people = await PlanningPeople.FindAsync(users, PriceChangeView.People(rows.Select(row => row.Price)), cancellationToken);

        var items = rows.Select(row => PriceChangeView.From(row.Price, row.ItemNameAr, row.ItemNameEn, people)).ToList();
        return TypedResults.Ok(new Response(items, page, pageSize, totalCount));
    }
}
