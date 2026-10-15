using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Archive.Common;

/// <summary>
/// Asks the module that owns a record kind which ids are on a project (<see cref="IProjectRecordOwnership"/>), so a
/// document of project A can never be linked to a record of project B. The link types are named like the kinds.
/// </summary>
internal static class RecordOwnership
{
    /// <summary>
    /// The ids among <paramref name="ids"/> that are <paramref name="kind"/> records on the project. A kind no module
    /// owns finds nothing, so an unknown pointer is refused rather than trusted.
    /// </summary>
    public static async Task<IReadOnlySet<int>> FindAsync(
        IEnumerable<IProjectRecordOwnership> owners, int projectId, string kind, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0 || owners.FirstOrDefault(owner => owner.Kinds.Contains(kind)) is not { } owner)
        {
            return new HashSet<int>();
        }

        return await owner.FindOnProjectAsync(projectId, kind, ids, cancellationToken);
    }
}
