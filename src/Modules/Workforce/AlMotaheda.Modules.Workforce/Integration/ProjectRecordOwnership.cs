using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Integration;

/// <summary>The Workforce module's answer to <see cref="IProjectRecordOwnership"/>: daily labor sheets and weekly settlements.</summary>
internal sealed class WorkforceRecordOwnership(WorkforceDbContext db) : IProjectRecordOwnership
{
    public IReadOnlyCollection<string> Kinds { get; } = [ProjectRecordKinds.LaborSheet, ProjectRecordKinds.PayrollSettlement];

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
    internal static IQueryable<int>? Query(WorkforceDbContext db, int projectId, string kind, int[] ids) => kind switch
    {
        ProjectRecordKinds.LaborSheet => db.DailySheets.AsNoTracking()
            .Where(sheet => sheet.ProjectId == projectId && ids.Contains(sheet.Id)).Select(sheet => sheet.Id),
        ProjectRecordKinds.PayrollSettlement => db.PayrollSettlements.AsNoTracking()
            .Where(settlement => settlement.ProjectId == projectId && ids.Contains(settlement.Id)).Select(settlement => settlement.Id),
        _ => null,
    };
}
