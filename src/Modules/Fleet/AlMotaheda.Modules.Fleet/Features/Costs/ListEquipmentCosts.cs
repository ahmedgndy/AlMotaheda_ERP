using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>
/// <c>GET /api/projects/{pid}/equipment-costs</c>: one page of a project's equipment costs, newest first. Only this
/// project's costs are read: costs of another project never show here.
/// </summary>
/// <remarks>
/// A caller without equipment See money sees only the costs they recorded themselves (they typed those amounts).
/// Every filter, the sort and the paging run in SQL.
/// </remarks>
internal static class ListEquipmentCosts
{
    internal sealed record Response(IReadOnlyList<EquipmentCostView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/equipment-costs", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName("ListEquipmentCosts")
            .WithSummary("List a project's equipment costs (filters: type, status, equipmentId, from, to). Without See money: only your own records");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        ISupplierDirectory suppliers,
        CancellationToken cancellationToken,
        string? type = null,
        string? status = null,
        int? equipmentId = null,
        string? from = null,
        string? to = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var typeFilter = Input.OptionalName<EquipmentCostType>(type, errors, "type");
        var statusFilter = Input.OptionalName<EquipmentCostStatus>(status, errors, "status");

        if (equipmentId is < 1)
        {
            errors.Add("equipmentId", "equipmentId is not valid.");
        }

        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        if (fromDate is { } f && toDate is { } t && t < f)
        {
            errors.Add("to", "to must not be before from.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        var filter = new EquipmentCostFilter(
            typeFilter,
            statusFilter,
            equipmentId,
            fromDate,
            toDate,
            RecordedBy: caller.SeesMoney ? null : caller.UserId);

        var query = EquipmentCostQueries.Filtered(db, projectId, filter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = await EquipmentCostView.FromAsync(rows, users, suppliers, cancellationToken);

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount));
    }
}

/// <summary>
/// <c>GET /api/projects/{pid}/equipment-costs/{id}</c>: one equipment cost. Without equipment See money only the
/// recorder may open it; anyone else gets the same 404 as for a cost that doesn't exist.
/// </summary>
internal static class GetEquipmentCost
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/equipment-costs/{costId:int}", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName("GetEquipmentCost")
            .WithSummary("One equipment cost of a project, with its payment");

    internal static async Task<Results<Ok<EquipmentCostView>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int costId,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        ISupplierDirectory suppliers,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        var row = await EquipmentCostQueries.One(db, projectId, costId).SingleOrDefaultAsync(cancellationToken);
        if (row is null || (!caller.SeesMoney && row.CreatedBy != caller.UserId))
        {
            return FleetProblems.CostNotFound();
        }

        return TypedResults.Ok((await EquipmentCostView.FromAsync([row], users, suppliers, cancellationToken))[0]);
    }
}
