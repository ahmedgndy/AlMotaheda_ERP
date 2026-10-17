using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Integration;

/// <summary>The Projects module's answer to <see cref="IProjectRecordOwnership"/>: its own project records.</summary>
internal sealed class ProjectRecordOwnership(ProjectsDbContext db) : IProjectRecordOwnership
{
    public IReadOnlyCollection<string> Kinds { get; } =
    [
        ProjectRecordKinds.Expense,
        ProjectRecordKinds.SupplierPayment,
        ProjectRecordKinds.DailyReport,
        ProjectRecordKinds.ReportRequest,
        ProjectRecordKinds.Budget,
        ProjectRecordKinds.Supplier,
    ];

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
    internal static IQueryable<int>? Query(ProjectsDbContext db, int projectId, string kind, int[] ids) => kind switch
    {
        ProjectRecordKinds.Expense => db.ProjectExpenses.AsNoTracking()
            .Where(record => record.ProjectId == projectId && ids.Contains(record.Id)).Select(record => record.Id),
        ProjectRecordKinds.SupplierPayment => db.SupplierPayments.AsNoTracking()
            .Where(record => record.ProjectId == projectId && ids.Contains(record.Id)).Select(record => record.Id),
        ProjectRecordKinds.DailyReport => db.DailyReports.AsNoTracking()
            .Where(record => record.ProjectId == projectId && ids.Contains(record.Id)).Select(record => record.Id),
        ProjectRecordKinds.ReportRequest => db.ReportRequests.AsNoTracking()
            .Where(record => record.ProjectId == projectId && ids.Contains(record.Id)).Select(record => record.Id),
        ProjectRecordKinds.Budget => db.Budgets.AsNoTracking()
            .Where(record => record.ProjectId == projectId && ids.Contains(record.Id)).Select(record => record.Id),
        // Suppliers are company-wide (overview 3.6): any supplier in the register may have documents on a project.
        ProjectRecordKinds.Supplier => db.Suppliers.AsNoTracking()
            .Where(record => ids.Contains(record.Id)).Select(record => record.Id),
        _ => null,
    };
}
