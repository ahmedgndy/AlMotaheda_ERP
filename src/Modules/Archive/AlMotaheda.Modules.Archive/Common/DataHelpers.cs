using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Common;

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

/// <summary>Recognises SQL Server constraint failures, so a lost race becomes a 409 instead of a 500.</summary>
internal static class SqlViolations
{
    private const int DuplicateKeyInUniqueIndex = 2601;
    private const int DuplicateKeyInUniqueConstraint = 2627;

    /// <summary>True when the save failed on the unique index called <paramref name="indexName"/>.</summary>
    public static bool IsUnique(DbUpdateException exception, string indexName) =>
        exception.InnerException is SqlException { Number: DuplicateKeyInUniqueIndex or DuplicateKeyInUniqueConstraint } sql
        && sql.Message.Contains(indexName, StringComparison.Ordinal);
}

/// <summary>The <c>page</c> and <c>pageSize</c> parameters every list takes.</summary>
internal static class Paging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxPage = 100_000;

    public static void Validate(int page, int pageSize, ValidationErrors errors)
    {
        if (page is < 1 or > MaxPage)
        {
            errors.Add("page", $"page must be between 1 and {MaxPage}.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors.Add("pageSize", $"pageSize must be between 1 and {MaxPageSize}.");
        }
    }
}
