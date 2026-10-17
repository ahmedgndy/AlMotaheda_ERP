using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
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
/// The duplicate guard of equipment costs (docs/cost-flow §2.7): the same machine, date, type, amount and invoice
/// number twice is one cost recorded twice, so the second is refused (409 <c>duplicate_cost</c>). A rejected cost
/// doesn't count: recording it again is how it is fixed.
/// </summary>
internal static class EquipmentCostDuplicates
{
    public static Task<bool> ExistsAsync(FleetDbContext db, EquipmentCostFields fields, int? exceptCostId, CancellationToken cancellationToken) =>
        db.EquipmentCosts.AsNoTracking().AnyAsync(
            cost => cost.EquipmentId == fields.EquipmentId
                && cost.CostDate == fields.CostDate
                && cost.Type == fields.Type
                && cost.Amount == fields.Amount
                && cost.InvoiceNo == fields.InvoiceNo
                && cost.Status != EquipmentCostStatus.Rejected
                && cost.Id != exceptCostId,
            cancellationToken);
}

/// <summary>
/// <c>POST /api/projects/{pid}/equipment-costs/{id}/item</c>: link a cost to a project item, move it to another one,
/// or unlink it (<c>projectItemId</c> empty). The cost stays one record: only its link changes, so the project total
/// never moves. Equipment Edit; allowed on any status, like a supply's item link.
/// </summary>
internal static class LinkEquipmentCostItem
{
    internal sealed record Request(int? ProjectItemId, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment-costs/{costId:int}/item", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.Edit)
            .WithName("LinkEquipmentCostItem")
            .WithSummary("Link an equipment cost to a project item, or unlink it (empty projectItemId). 409 item_inactive");

    internal static async Task<Results<Ok<EquipmentCostView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int costId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IProjectItems items,
        IUserDirectory users,
        ISupplierDirectory suppliers,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (request.ProjectItemId is < 1)
        {
            errors.Add("projectItemId", "projectItemId is not valid.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return FleetProblems.CompletedProject();
        }

        var cost = await db.EquipmentCosts.SingleOrDefaultAsync(candidate => candidate.Id == costId && candidate.ProjectId == projectId, cancellationToken);
        if (cost is null)
        {
            return FleetProblems.CostNotFound();
        }

        if (request.ProjectItemId is { } itemId && itemId != cost.ProjectItemId)
        {
            // Asked from Projects: an item of another project is "not found", never linked.
            var found = await items.FindAsync(projectId, [itemId], cancellationToken);
            if (!found.TryGetValue(itemId, out var item))
            {
                errors.Add("projectItemId", "The project item was not found on this project.");
                return FleetProblems.Validation(errors);
            }

            if (!item.IsActive)
            {
                return FleetProblems.InactiveItem();
            }
        }

        var now = clock.UtcNow;
        cost.ProjectItemId = request.ProjectItemId;
        cost.ItemLinkedAt = request.ProjectItemId is null ? null : now;
        cost.ItemLinkedBy = request.ProjectItemId is null ? null : caller.UserId;
        db.Entry(cost).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (cost.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The item was deleted after the check: a lost race is a 409, never a 500.
            return FleetProblems.InactiveItem();
        }

        log.CostItemLinked(caller.UserId, projectId, cost.Id, cost.ProjectItemId);
        var view = await EquipmentCostView.LoadAsync(db, users, suppliers, projectId, cost.Id, cancellationToken);
        return TypedResults.Ok(view!);
    }
}

/// <summary>
/// Fleet's answer to <see cref="IItemCostSource"/>: approved equipment costs per project item, with the same
/// settlement rule as the equipment screens (<see cref="EquipmentCostSettlement"/>), so an item's equipment figure and
/// the project's equipment group can't disagree.
/// </summary>
internal sealed class EquipmentItemCosts(FleetDbContext db) : IItemCostSource
{
    public string Group => CostGroups.Equipment;

    public async Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken)
    {
        var rows = await Rows(projectId, itemId: null, allItems: true).ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.ItemId)
            .Select(group => new ItemCostTotal(
                group.Key,
                group.Sum(row => EquipmentCostSettlement.Recognized(row.Settlement)),
                group.Sum(row => EquipmentCostSettlement.Paid(row.Settlement)),
                group.Count(),
                group.Select(row => row.Settlement.EquipmentId).Distinct().Count()))
            .ToList();
    }

    public async Task<IReadOnlyList<ItemSourceLine>> LinesAsync(int projectId, int? itemId, CancellationToken cancellationToken)
    {
        var rows = await Rows(projectId, itemId, allItems: false).ToListAsync(cancellationToken);
        return rows
            .OrderBy(row => row.Settlement.CostDate)
            .ThenBy(row => row.Settlement.CostId)
            .Select(row =>
            {
                var cost = EquipmentCostSettlement.Recognized(row.Settlement);
                var paid = EquipmentCostSettlement.Paid(row.Settlement);
                return new ItemSourceLine(
                    "EquipmentCost",
                    row.Settlement.CostId,
                    row.Settlement.CostDate,
                    row.Settlement.Type.ToString(),
                    row.Description,
                    row.Settlement.EquipmentId,
                    row.EquipmentNameAr,
                    row.EquipmentNameEn,
                    row.Settlement.Liters ?? row.Quantity,
                    row.Settlement.Liters is null ? null : "Liter",
                    cost,
                    paid,
                    cost - paid,
                    PaymentStates.Of(cost, paid));
            })
            .ToList();
    }

    public Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken) =>
        db.EquipmentCosts.AsNoTracking().AnyAsync(cost => cost.ProjectId == projectId && cost.ProjectItemId == itemId, cancellationToken);

    private sealed record ItemRow(
        int? ItemId, EquipmentCostSettlementRow Settlement, string Description, decimal? Quantity, string EquipmentNameAr, string EquipmentNameEn);

    /// <summary>Approved costs of the project: of one item (or not linked, when <paramref name="itemId"/> is null), or of all.</summary>
    private IQueryable<ItemRow> Rows(int projectId, int? itemId, bool allItems) =>
        db.EquipmentCosts.AsNoTracking()
            .Where(cost => cost.ProjectId == projectId
                && cost.Status == EquipmentCostStatus.Approved
                && (allItems || cost.ProjectItemId == itemId))
            .Select(cost => new ItemRow(
                cost.ProjectItemId,
                new EquipmentCostSettlementRow(
                    cost.Id,
                    cost.EquipmentId,
                    cost.Type,
                    cost.Status,
                    cost.Channel,
                    cost.CostDate,
                    cost.Amount + (cost.Adjustments.Where(adjustment => adjustment.Status == AdjustmentStatus.Approved)
                        .Sum(adjustment => (decimal?)adjustment.Amount) ?? 0),
                    cost.Liters,
                    cost.PaidOn != null),
                cost.Description,
                cost.Quantity,
                cost.Equipment.NameAr,
                cost.Equipment.NameEn));
}
