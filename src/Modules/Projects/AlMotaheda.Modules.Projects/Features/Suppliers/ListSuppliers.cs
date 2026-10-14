using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Suppliers;

/// <summary>
/// <c>GET /api/suppliers</c>: one page of the company's suppliers, for the suppliers list and the supplier pickers
/// on the supply and payment forms. Suppliers are company-wide (overview 3.6), so there is no project scope here.
/// </summary>
internal static class ListSuppliers
{
    internal sealed record Response(IReadOnlyList<SupplierView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/suppliers", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("ListSuppliers")
            .WithSummary("List the company's suppliers (search: names or phone; status: Active or Inactive; kind)");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        ProjectsDbContext db,
        CancellationToken cancellationToken,
        string? search = null,
        string? status = null,
        string? kind = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);

        SupplierStatus? statusFilter = null;
        if (Input.Clean(status) is { } statusText)
        {
            if (Input.TryParseName<SupplierStatus>(statusText, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add("status", "status must be Active or Inactive.");
            }
        }

        SupplierKind? kindFilter = null;
        if (Input.Clean(kind) is { } kindText)
        {
            if (Input.TryParseName<SupplierKind>(kindText, out var parsed))
            {
                kindFilter = parsed;
            }
            else
            {
                errors.Add("kind", $"kind must be one of: {string.Join(", ", Enum.GetNames<SupplierKind>())}.");
            }
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var query = SupplierQueries.Search(db, Input.Clean(search), statusFilter, kindFilter);
        var totalCount = await query.CountAsync(cancellationToken);
        var suppliers = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(suppliers.Select(SupplierView.From).ToList(), page, pageSize, totalCount));
    }
}
