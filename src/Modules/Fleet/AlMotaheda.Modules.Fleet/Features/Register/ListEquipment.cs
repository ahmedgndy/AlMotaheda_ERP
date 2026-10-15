using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Register;

/// <summary>
/// <c>GET /api/equipment</c>: one page of the company's equipment register. Rates are shown only with equipment
/// See money. Every filter, the sort and the paging run in SQL.
/// </summary>
internal static class ListEquipment
{
    internal sealed record Response(IReadOnlyList<EquipmentListItem> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/equipment", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName("ListEquipment")
            .WithSummary("List the equipment register (search: asset no., names, type, serial; filters: category, ownership, "
                         + "status, available=true for machines in service and on no project)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        FleetDbContext db,
        ISupplierDirectory suppliers,
        CancellationToken cancellationToken,
        string? search = null,
        string? category = null,
        string? ownership = null,
        string? status = null,
        bool? available = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var categoryFilter = Input.OptionalName<EquipmentCategory>(category, errors, "category");
        var ownershipFilter = Input.OptionalName<EquipmentOwnership>(ownership, errors, "ownership");
        var statusFilter = Input.OptionalName<EquipmentStatus>(status, errors, "status");
        var text = Input.OptionalText(search, EquipmentFilterLimits.SearchMaxLength, errors, "search", "search");
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var query = EquipmentQueries.Search(db, new EquipmentFilter(text, categoryFilter, ownershipFilter, statusFilter, available));
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var lessors = await Lookups.SuppliersAsync(suppliers, rows.Select(row => row.CurrentRental?.SupplierId), cancellationToken);

        var items = rows.Select(row => EquipmentListItem.From(row, lessors, caller.SeesMoney)).ToList();
        return TypedResults.Ok(new Response(items, page, pageSize, totalCount));
    }
}

internal static class EquipmentFilterLimits
{
    public const int SearchMaxLength = 100;
}
