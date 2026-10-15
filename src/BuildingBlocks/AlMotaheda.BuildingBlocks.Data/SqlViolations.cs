using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>Recognises SQL Server constraint failures, so a lost race becomes a 409 instead of a 500.</summary>
public static class SqlViolations
{
    private const int DuplicateKeyInUniqueIndex = 2601;
    private const int DuplicateKeyInUniqueConstraint = 2627;
    private const int ForeignKeyConflict = 547;

    /// <summary>True when the save failed on the unique index called <paramref name="indexName"/>.</summary>
    public static bool IsUnique(DbUpdateException exception, string indexName) =>
        exception.InnerException is SqlException { Number: DuplicateKeyInUniqueIndex or DuplicateKeyInUniqueConstraint } sql
        && sql.Message.Contains(indexName, StringComparison.Ordinal);

    /// <summary>True when the save failed on any unique index or unique constraint.</summary>
    public static bool IsAnyUnique(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: DuplicateKeyInUniqueIndex or DuplicateKeyInUniqueConstraint };

    /// <summary>True when the save failed because another row still points to this one (or a check failed).</summary>
    public static bool IsReferenceOrCheck(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: ForeignKeyConflict };
}
