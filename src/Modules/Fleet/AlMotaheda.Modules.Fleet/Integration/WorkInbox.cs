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

            // Repair requests waiting for a decision (docs/cost-flow §2.7): no amount, a request is never a cost.
            var repairs = await db.RepairRequests.AsNoTracking()
                .Where(request => ids.Contains(request.ProjectId) && request.Status == RepairRequestStatus.Open && request.RequestedBy != me)
                .OrderBy(request => request.RequestedAt)
                .Take(query.Limit)
                .Select(request => new { request.Id, request.ProjectId, request.EquipmentId, request.RequestedAt, request.RequestedBy, request.Priority, request.Equipment.NameAr, request.Equipment.NameEn })
                .ToListAsync(cancellationToken);
            items.AddRange(repairs.Select(request => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.RepairRequest, request.ProjectId, request.Id, request.EquipmentId,
                DateOnly.FromDateTime(request.RequestedAt), request.RequestedAt, $"{request.Priority} · {Name(request.NameAr, request.NameEn)}", null,
                request.RequestedBy, null)));
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

        return items;
    }

    private static string Name(string ar, string en) => string.IsNullOrWhiteSpace(en) ? ar : $"{ar} / {en}";
}
