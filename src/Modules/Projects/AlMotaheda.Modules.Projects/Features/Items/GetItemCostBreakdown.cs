using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Budgets;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>
/// <c>GET /api/projects/{pid}/items/{itemId}/cost-breakdown</c>: everything a work item has cost, from every source,
/// for one main item with its sub-items (or one sub-item): purchases and site expenses with their supplier balances,
/// internal labor, subcontract work and commitments, machinery (equipment costs linked to the item), the approved
/// budget against the actual cost, and the transaction history.
/// </summary>
/// <remarks>
/// <para><c>totals</c> is never filtered: it equals the item's row in the items table (with its sub-items for a main
/// item), so the two screens can't disagree. The filters (<c>from</c>, <c>to</c>, <c>subItemId</c>,
/// <c>supplierId</c>, <c>workerId</c>, <c>subcontractorId</c>, <c>equipmentId</c>, <c>type</c>) narrow <c>transactions</c>,
/// <c>filtered</c> and the per-party summaries. Only approved records are cost; pending ones are counted apart.</para>
/// <para>projects See money and project scope, like the items table. One query per source, never one per sub-item.</para>
/// </remarks>
internal static class GetItemCostBreakdown
{
    internal sealed record Response(
        ItemRow Item,
        IReadOnlyList<ItemRow> SubItems,
        ItemFiguresView Totals,
        BreakdownBudget Budget,
        BreakdownCommitments Commitments,
        BreakdownPending Pending,
        BreakdownTotals Filtered,
        IReadOnlyList<BreakdownSupplier> Suppliers,
        IReadOnlyList<BreakdownWorker> Workers,
        IReadOnlyList<BreakdownSubcontractor> Subcontractors,
        IReadOnlyList<BreakdownMachine> Machines,
        IReadOnlyList<BreakdownTransaction> Transactions,
        int Page,
        int PageSize,
        int TotalCount);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/items/{itemId:int}/cost-breakdown", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetItemCostBreakdown")
            .WithSummary("A work item's cost breakdown with its sub-items: suppliers, labor, subcontractors, machinery, budget, commitments and transactions (filters: from, to, subItemId, supplierId, workerId, subcontractorId, equipmentId, type)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int itemId,
        HttpContext httpContext,
        IProjectAccess access,
        ProjectsDbContext db,
        ItemsLedgerSource source,
        ILaborItemCosts labor,
        ISubcontractItemCosts subcontracts,
        IEquipmentItemCosts equipment,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null,
        int? subItemId = null,
        int? supplierId = null,
        int? workerId = null,
        int? subcontractorId = null,
        int? equipmentId = null,
        string? type = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (ProjectCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fromDate = Input.OptionalDate(from, errors, "from", "The first date");
        var toDate = Input.OptionalDate(to, errors, "to", "The last date");
        if (fromDate > toDate)
        {
            errors.Add("to", "The last date can't be before the first date.");
        }

        string? parsedType = null;
        if (Input.Clean(type) is not null)
        {
            if (BreakdownTypes.TryParse(type, out var known))
            {
                parsedType = known;
            }
            else
            {
                errors.Add("type", $"type must be one of: {string.Join(", ", BreakdownTypes.All)}.");
            }
        }

        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var data = await source.LoadAsync(projectId, cancellationToken);
        var item = data.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null)
        {
            return CoreProblems.ItemNotFound();
        }

        var ids = data.Tree.SelfAndSubItems(item.Id);
        if (subItemId is { } wanted && !ids.Contains(wanted))
        {
            errors.Add("subItemId", "This is not the item or one of its sub-items.");
            return ProjectProblems.Validation(errors);
        }

        var idArray = ids.ToArray();
        var approved = await ExpenseQueries.LinesOfItems(db, projectId, idArray, approved: true).ToListAsync(cancellationToken);
        var pending = await ExpenseQueries.LinesOfItems(db, projectId, idArray, approved: false).ToListAsync(cancellationToken);
        var laborLines = await labor.LinesByItemAsync(projectId, ids, cancellationToken);
        var workLines = await subcontracts.LinesByItemAsync(projectId, ids, cancellationToken);
        var commitments = await subcontracts.CommitmentsByItemAsync(projectId, ids, cancellationToken);
        var machineLines = await equipment.LinesByItemAsync(projectId, ids, cancellationToken);
        var budget = await BudgetedAsync(db, projectId, idArray, cancellationToken);

        var all = ItemCostBreakdown.FromExpenses(approved)
            .Concat(ItemCostBreakdown.FromLabor(laborLines))
            .Concat(ItemCostBreakdown.FromSubcontracts(workLines))
            .Concat(ItemCostBreakdown.FromEquipment(machineLines));
        var filter = new BreakdownFilter(fromDate, toDate, subItemId, supplierId, workerId, subcontractorId, parsedType, equipmentId);
        var rows = ItemCostBreakdown.Filter(all, filter);

        // A commitment has no date (it is "as of now"): only the item and subcontractor filters apply to it.
        var keptCommitments = commitments
            .Where(line => (subItemId is not { } sub || line.ItemId == sub) && (subcontractorId is not { } party || line.SubcontractorId == party))
            .Where(_ => parsedType is null || parsedType == BreakdownTypes.Subcontract)
            .Where(_ => supplierId is null && workerId is null && equipmentId is null)
            .ToList();

        var totals = data.Tree.WithSubItems(item, data.Ledger) ?? data.Ledger.Of(item.Id);
        var subItems = data.Tree.SubItemIds(item.Id).Select(child => data.RowOf(data.Items.First(candidate => candidate.Id == child))).ToList();

        return TypedResults.Ok(new Response(
            data.RowOf(item),
            subItems,
            ItemFiguresView.From(totals),
            ItemCostBreakdown.Budget(budget.BudgetId, budget.Version, budget.BudgetId is not null, budget.Amount, totals.TotalCost),
            new BreakdownCommitments(commitments.Sum(line => line.Remaining), commitments),
            new BreakdownPending(pending.Count, pending.Sum(row => row.Amount)),
            ItemCostBreakdown.Totals(rows),
            ItemCostBreakdown.Suppliers(rows),
            ItemCostBreakdown.Workers(rows),
            ItemCostBreakdown.Subcontractors(rows, keptCommitments),
            ItemCostBreakdown.Machines(rows),
            rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            page,
            pageSize,
            rows.Count));
    }

    private sealed record BudgetedAmount(int? BudgetId, int? Version, decimal Amount);

    /// <summary>The approved budget's lines on these items. No approved budget: nothing is budgeted.</summary>
    private static async Task<BudgetedAmount> BudgetedAsync(ProjectsDbContext db, int projectId, int[] itemIds, CancellationToken cancellationToken)
    {
        var approved = await BudgetQueries.Approved(db, projectId).FirstOrDefaultAsync(cancellationToken);
        if (approved is null)
        {
            return new BudgetedAmount(null, null, 0);
        }

        var amount = await BudgetQueries.LinesOf(db, projectId, [approved.Id])
            .Where(line => line.ProjectItemId != null && itemIds.Contains(line.ProjectItemId.Value))
            .SumAsync(line => line.Amount, cancellationToken);
        return new BudgetedAmount(approved.Id, approved.Version, amount);
    }
}
