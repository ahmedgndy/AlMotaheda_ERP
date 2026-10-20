using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>
/// <c>GET /api/projects/{pid}/items/{itemId}</c>: one item with its approved supplier, subcontract, labor and
/// equipment lines, the supplier records still waiting for review, and the same totals as its row in the table.
/// </summary>
internal static class GetProjectItem
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/items/{itemId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetProjectItem")
            .WithSummary("One project item with its supplier, subcontract, labor and equipment lines and its totals");

    internal static async Task<Results<Ok<ItemDetail>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int itemId,
        HttpContext httpContext,
        IProjectAccess access,
        ItemDetails details,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var detail = await details.LoadAsync(projectId, itemId, cancellationToken);
        return detail is null ? CoreProblems.ItemNotFound() : TypedResults.Ok(detail);
    }
}

/// <summary>
/// <c>GET /api/projects/{pid}/items/unallocated</c>: the "غير مربوط ببند بعد" group, in the same shape as an item:
/// the approved supplier, subcontract and labor records that have no item yet, and their totals. Equipment costs on
/// no item are not here: they are costs of the whole site (other costs of the items table).
/// </summary>
internal static class GetUnallocatedItems
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/items/unallocated", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetUnallocatedItemRecords")
            .WithSummary("The records not linked to any project item yet, with their totals");

    internal static async Task<Results<Ok<ItemDetail>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        IProjectAccess access,
        ItemDetails details,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        return TypedResults.Ok(await details.LoadAsync(projectId, itemId: null, cancellationToken));
    }
}

/// <summary>
/// Builds an item detail, or the not-linked detail, from the same ledger run as the table, so the totals at the
/// bottom of a detail always equal the row in the table.
/// </summary>
internal sealed class ItemDetails(
    ProjectsDbContext db, ItemsLedgerSource source, ILaborItemCosts labor, ISubcontractItemCosts subcontracts, IEquipmentItemCosts equipment)
{
    /// <returns><c>null</c> when <paramref name="itemId"/> is set and the item is not on this project.</returns>
    public async Task<ItemDetail?> LoadAsync(int projectId, int? itemId, CancellationToken cancellationToken)
    {
        var data = await source.LoadAsync(projectId, cancellationToken);

        ItemHeader? item = null;
        if (itemId is { } id)
        {
            item = data.Items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null)
            {
                return null;
            }
        }

        var figures = itemId is { } wanted ? data.Ledger.Of(wanted) : data.Ledger.Unallocated;

        var approved = await ExpenseQueries.ItemLines(db, projectId, itemId, approved: true).ToListAsync(cancellationToken);
        var pending = await ExpenseQueries.ItemLines(db, projectId, itemId, approved: false).ToListAsync(cancellationToken);
        var subcontractLines = await subcontracts.LinesAsync(projectId, itemId, cancellationToken);
        var laborLines = await labor.LinesAsync(projectId, itemId, cancellationToken);
        IReadOnlyList<EquipmentItemLine> equipmentLines = item is null
            ? []
            : (await equipment.LinesByItemAsync(projectId, [item.Id], cancellationToken)).GetValueOrDefault(item.Id) ?? [];

        var subItems = item is null
            ? []
            : data.Tree.SubItemIds(item.Id).Select(child => data.RowOf(data.Items.First(candidate => candidate.Id == child))).ToList();
        var withSubItems = item is null ? null : data.Tree.WithSubItems(item, data.Ledger);

        return new ItemDetail(
            item is null ? null : data.RowOf(item),
            approved.Select(SupplierLine.From).ToList(),
            subcontractLines,
            laborLines,
            pending.Select(SupplierLine.From).ToList(),
            ItemFiguresView.From(figures),
            subItems,
            withSubItems is null ? null : ItemFiguresView.From(withSubItems),
            equipmentLines);
    }
}
