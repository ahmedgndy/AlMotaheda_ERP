using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Integration;

/// <summary>
/// The Subcontractors module's answer to <see cref="IProjectRecordOwnership"/>: contracts, and payments through
/// the contract they were made under.
/// </summary>
internal sealed class SubcontractRecordOwnership(SubcontractorsDbContext db) : IProjectRecordOwnership
{
    public IReadOnlyCollection<string> Kinds { get; } = [ProjectRecordKinds.SubContract, ProjectRecordKinds.SubPayment];

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
    internal static IQueryable<int>? Query(SubcontractorsDbContext db, int projectId, string kind, int[] ids) => kind switch
    {
        ProjectRecordKinds.SubContract => db.SubContracts.AsNoTracking()
            .Where(contract => contract.ProjectId == projectId && ids.Contains(contract.Id)).Select(contract => contract.Id),
        ProjectRecordKinds.SubPayment => db.SubPayments.AsNoTracking()
            .Where(payment => payment.Contract.ProjectId == projectId && ids.Contains(payment.Id)).Select(payment => payment.Id),
        _ => null,
    };
}
