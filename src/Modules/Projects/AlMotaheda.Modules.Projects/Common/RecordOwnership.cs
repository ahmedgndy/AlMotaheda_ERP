using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Asks the module that owns a record kind which ids are on a project (<see cref="IProjectRecordOwnership"/>). Used
/// where this module stores a pointer to another module's record, so a pointer never crosses projects.
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
