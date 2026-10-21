using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Integration;

/// <summary>The Archive's answer to "does this record have evidence": live documents linked to it (<see cref="IRecordDocuments"/>).</summary>
internal sealed class RecordDocuments(ArchiveDbContext db) : IRecordDocuments
{
    public async Task<IReadOnlyDictionary<int, int>> CountAsync(string entityType, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0 || !Enum.TryParse<LinkedEntityType>(entityType, ignoreCase: false, out var kind))
        {
            return new Dictionary<int, int>();
        }

        return await db.DocumentLinks.AsNoTracking()
            .Where(link => link.EntityType == kind && ids.Contains(link.EntityId) && link.Document.DeletedAt == null)
            .GroupBy(link => link.EntityId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Key, row => row.Count, cancellationToken);
    }
}
