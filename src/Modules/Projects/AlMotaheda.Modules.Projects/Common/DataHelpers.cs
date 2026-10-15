using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Recognises unique-index violations, so a lost race becomes a 409 instead of a 500.</summary>
internal static class UniqueViolations
{
    private static readonly string[] KnownIndexes =
    [
        ProjectConfiguration.CodeIndex,
        ProjectMemberConfiguration.ActiveUserIndex,
        ProjectMemberConfiguration.ActiveManagerIndex,
    ];

    /// <summary>
    /// The name of the unique index that was violated, <c>"other"</c> for an unknown unique index, or <c>null</c>
    /// when the failure is not a unique violation.
    /// </summary>
    public static string? IndexOf(DbUpdateException exception)
    {
        if (!SqlViolations.IsAnyUnique(exception))
        {
            return null;
        }

        return KnownIndexes.FirstOrDefault(name => SqlViolations.IsUnique(exception, name)) ?? "other";
    }
}
