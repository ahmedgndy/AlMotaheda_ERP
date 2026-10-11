using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Converts the <c>rowVersion</c> sent by clients (base64) back to bytes.</summary>
internal static class RowVersions
{
    public static string Format(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static byte[]? Parse(string? value, ValidationErrors errors, string field = "rowVersion")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(field, "rowVersion is required. Send the value from your last read of this record.");
            return null;
        }

        var buffer = new byte[8];
        if (!Convert.TryFromBase64String(value.Trim(), buffer, out var written) || written != 8)
        {
            errors.Add(field, "rowVersion is not valid.");
            return null;
        }

        return buffer;
    }
}

/// <summary>Recognises unique-index violations, so a lost race becomes a 409 instead of a 500.</summary>
internal static class UniqueViolations
{
    private const int DuplicateKeyInUniqueIndex = 2601;
    private const int DuplicateKeyInUniqueConstraint = 2627;

    /// <summary>The name of the unique index that was violated, or <c>null</c> for any other failure.</summary>
    public static string? IndexOf(DbUpdateException exception)
    {
        if (exception.InnerException is not SqlException { Number: DuplicateKeyInUniqueIndex or DuplicateKeyInUniqueConstraint } sql)
        {
            return null;
        }

        string[] known =
        [
            ProjectConfiguration.CodeIndex,
            ProjectMemberConfiguration.ActiveUserIndex,
            ProjectMemberConfiguration.ActiveManagerIndex,
        ];

        return known.FirstOrDefault(name => sql.Message.Contains(name, StringComparison.Ordinal)) ?? "other";
    }
}
