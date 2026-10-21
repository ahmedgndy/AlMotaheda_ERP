using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>
/// Fleet's part of the work inbox: equipment costs recorded for review (equipment Approve, never the recorder) and
/// costs returned to their recorder. Amounts only with equipment See money.
/// </summary>
internal sealed class FleetInboxSource(FleetDbContext db) : IWorkInboxSource
{
    public async Task<IReadOnlyList<WorkInboxItem>> ListAsync(WorkInboxQuery query, CancellationToken cancellationToken)
    {
        var ids = query.ProjectIds.ToList();
        var me = query.UserId;
        var items = new List<WorkInboxItem>();
        var seesMoney = query.Caller.HasPermission(FleetPermissions.Module, PermissionAction.SeeMoney);

        if (query.Caller.HasPermission(FleetPermissions.Module, PermissionAction.Approve))
        {
            var recorded = await db.EquipmentCosts.AsNoTracking()
                .Where(cost => ids.Contains(cost.ProjectId) && cost.Status == EquipmentCostStatus.Recorded && cost.CreatedBy != me)
                .OrderBy(cost => cost.CreatedAt)
                .Take(query.Limit)
                .Select(cost => new { cost.Id, cost.ProjectId, cost.CostDate, cost.Type, cost.Equipment.NameAr, cost.Equipment.NameEn, cost.Amount, cost.CreatedAt, cost.CreatedBy })
                .ToListAsync(cancellationToken);
            items.AddRange(recorded.Select(cost => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.EquipmentCost, cost.ProjectId, cost.Id, null, cost.CostDate, cost.CreatedAt,
                $"{cost.Type} · {Name(cost.NameAr, cost.NameEn)}", seesMoney ? cost.Amount : null, cost.CreatedBy, null)));
        }

        var returned = await db.EquipmentCosts.AsNoTracking()
            .Where(cost => ids.Contains(cost.ProjectId) && cost.Status == EquipmentCostStatus.Returned && cost.CreatedBy == me)
            .OrderBy(cost => cost.ReviewedAt)
            .Take(query.Limit)
            .Select(cost => new { cost.Id, cost.ProjectId, cost.CostDate, cost.Type, cost.Equipment.NameAr, cost.Equipment.NameEn, cost.Amount, cost.ReviewedAt, cost.CreatedAt, cost.ReviewedBy, cost.ReviewNote })
            .ToListAsync(cancellationToken);
        items.AddRange(returned.Select(cost => new WorkInboxItem(
            WorkInboxReasons.Returned, WorkInboxRecordTypes.EquipmentCost, cost.ProjectId, cost.Id, null, cost.CostDate, cost.ReviewedAt ?? cost.CreatedAt,
            $"{cost.Type} · {Name(cost.NameAr, cost.NameEn)}", seesMoney ? cost.Amount : null, cost.ReviewedBy, cost.ReviewNote)));

        // Vehicle expense claims of vehicles on the caller's projects, waiting for a fuel reviewer (never the submitter).
        // A claim of a vehicle on no project is in the claims queue and the notifications, not in the project inbox.
        if (query.Caller.HasPermission(FleetPermissions.Fuel, PermissionAction.Approve))
        {
            var seesClaimMoney = seesMoney || query.Caller.HasPermission(FleetPermissions.Fuel, PermissionAction.SeeMoney);
            var claims = await db.VehicleExpenses.AsNoTracking()
                .Where(claim => claim.ProjectId != null && ids.Contains(claim.ProjectId.Value) && claim.CreatedBy != me
                                && (claim.Status == VehicleExpenseStatus.Submitted || claim.Status == VehicleExpenseStatus.Escalated))
                .OrderBy(claim => claim.SubmittedAt)
                .Take(query.Limit)
                .Select(claim => new { claim.Id, claim.ProjectId, claim.ExpenseDate, claim.Type, claim.Vehicle.Plate, claim.Amount, claim.SubmittedAt, claim.CreatedBy, claim.Status })
                .ToListAsync(cancellationToken);
            items.AddRange(claims.Select(claim => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.VehicleExpense, claim.ProjectId!.Value, claim.Id, null, claim.ExpenseDate, claim.SubmittedAt,
                $"{claim.Type} · {claim.Plate}{(claim.Status == VehicleExpenseStatus.Escalated ? " · escalated" : "")}", seesClaimMoney ? claim.Amount : null, claim.CreatedBy, null)));
        }

        // Claims sent back for more evidence, to their submitter.
        var evidence = await db.VehicleExpenses.AsNoTracking()
            .Where(claim => claim.ProjectId != null && ids.Contains(claim.ProjectId.Value) && claim.CreatedBy == me && claim.Status == VehicleExpenseStatus.EvidenceRequested)
            .OrderBy(claim => claim.ReviewedAt)
            .Take(query.Limit)
            .Select(claim => new { claim.Id, claim.ProjectId, claim.ExpenseDate, claim.Type, claim.Vehicle.Plate, claim.Amount, claim.ReviewedAt, claim.SubmittedAt, claim.ReviewedBy, claim.ReviewNote })
            .ToListAsync(cancellationToken);
        items.AddRange(evidence.Select(claim => new WorkInboxItem(
            WorkInboxReasons.Returned, WorkInboxRecordTypes.VehicleExpense, claim.ProjectId!.Value, claim.Id, null, claim.ExpenseDate, claim.ReviewedAt ?? claim.SubmittedAt,
            $"{claim.Type} · {claim.Plate}", claim.Amount, claim.ReviewedBy, claim.ReviewNote)));

        return items;
    }

    private static string Name(string ar, string en) => string.IsNullOrWhiteSpace(en) ? ar : $"{ar} / {en}";
}
