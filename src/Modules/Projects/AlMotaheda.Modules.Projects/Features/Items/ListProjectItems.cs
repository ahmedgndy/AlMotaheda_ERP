using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>
/// <c>GET /api/projects/{pid}/items</c>: the project items table ("بنود"), with the totals over all items, the
/// "not linked to an item yet" group, and the reconciliation to the project's recognized cost.
/// </summary>
/// <remarks>
/// The search and <c>includeInactive</c> only choose which rows are listed. <c>totals</c> always covers every item
/// of the project, so the footer doesn't change while someone searches. The figures of every row come from one
/// run of <see cref="ItemsLedger"/> over the project's records (decision G5).
/// </remarks>
internal static class ListProjectItems
{
    internal sealed record Response(
        IReadOnlyList<ItemRow> Items,
        ItemFiguresView Totals,
        ItemFiguresView Unallocated,
        ItemsReconciliation Reconciliation);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/items", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("ListProjectItems")
            .WithSummary("The project items table with cost, paid and outstanding per item (search: names; includeInactive)");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        IProjectAccess access,
        ItemsLedgerSource source,
        IEnumerable<IProjectCostSource> costSources,
        CancellationToken cancellationToken,
        string? search = null,
        bool includeInactive = false)
    {
        if (ProjectCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var data = await source.LoadAsync(projectId, cancellationToken);
        var text = Input.Clean(search);

        // Screen order: each main item followed by its sub-items.
        var rows = data.Tree.Ordered
            .Where(item => includeInactive || item.Status == ProjectItemStatus.Active)
            .Where(item => text is null
                || item.NameAr.Contains(text, StringComparison.OrdinalIgnoreCase)
                || item.NameEn.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(data.RowOf)
            .ToList();

        var reconciliation = await ReconcileAsync(projectId, data, costSources, cancellationToken);

        return TypedResults.Ok(new Response(
            rows,
            ItemFiguresView.From(data.Ledger.Totals),
            ItemFiguresView.From(data.Ledger.Unallocated),
            reconciliation));
    }

    /// <summary>
    /// projectRecognizedCost = items + not linked + other costs, where other costs are the site expenses outside
    /// the items screens and the equipment costs that Fleet reports. Equipment is never split over items (F1).
    /// </summary>
    internal static async Task<ItemsReconciliation> ReconcileAsync(
        int projectId, ItemsLedgerData data, IEnumerable<IProjectCostSource> costSources, CancellationToken cancellationToken)
    {
        decimal equipment = 0;
        foreach (var costSource in costSources.Where(candidate => candidate.Group == CostGroups.Equipment))
        {
            equipment += (await costSource.SummarizeAsync(projectId, null, null, cancellationToken)).Recognized;
        }

        var items = data.Ledger.Totals.TotalCost;
        var unallocated = data.Ledger.Unallocated.TotalCost;
        var other = data.OtherSiteExpenses + equipment;

        return new ItemsReconciliation(items, unallocated, other, items + unallocated + other);
    }
}
