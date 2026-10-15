namespace AlMotaheda.BuildingBlocks.Validation;

/// <summary>Converts the <c>rowVersion</c> sent by clients (base64) back to bytes.</summary>
public static class RowVersions
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
