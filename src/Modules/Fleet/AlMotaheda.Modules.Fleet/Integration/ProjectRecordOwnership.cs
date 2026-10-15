using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>The Fleet module's answer to <see cref="IProjectRecordOwnership"/>: equipment costs and machines.</summary>
/// <remarks>
/// A machine is company-wide, so "on this project" means it has been assigned to the project at some time (open or
/// ended stay). A document or a daily report can still point to a machine that has since moved to another site.
/// </remarks>
internal sealed class FleetRecordOwnership(FleetDbContext db) : IProjectRecordOwnership
{
    public IReadOnlyCollection<string> Kinds { get; } = [ProjectRecordKinds.Equipment, ProjectRecordKinds.EquipmentCost];

    public async Task<IReadOnlySet<int>> FindOnProjectAsync(
        int projectId, string kind, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0 || Query(db, projectId, kind, ids.Distinct().ToArray()) is not { } query)
        {
            return new HashSet<int>();
        }

        return (await query.ToListAsync(cancellationToken)).ToHashSet();
    }

    /// <summary>The ids of <paramref name="kind"/> on the project, or <c>null</c> for a kind this module doesn't own.</summary>
    internal static IQueryable<int>? Query(FleetDbContext db, int projectId, string kind, int[] ids) => kind switch
    {
        ProjectRecordKinds.Equipment => db.EquipmentAssignments.AsNoTracking()
            .Where(stay => stay.ProjectId == projectId && ids.Contains(stay.EquipmentId)).Select(stay => stay.EquipmentId).Distinct(),
        ProjectRecordKinds.EquipmentCost => db.EquipmentCosts.AsNoTracking()
            .Where(cost => cost.ProjectId == projectId && ids.Contains(cost.Id)).Select(cost => cost.Id),
        _ => null,
    };
}
