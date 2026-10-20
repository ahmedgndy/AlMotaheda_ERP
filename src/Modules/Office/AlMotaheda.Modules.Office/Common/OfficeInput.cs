namespace AlMotaheda.Modules.Office.Common;

/// <summary>Input rules the shared <see cref="Input"/> and <see cref="NumberInput"/> don't have.</summary>
internal static class OfficeInput
{
    /// <summary>An optional amount that may be zero (a discount, a tax), with at most two decimals. Missing = 0.</summary>
    public static decimal NonNegativeMoney(decimal? value, ValidationErrors errors, string field, string label)
    {
        if (value is null)
        {
            return 0m;
        }

        if (value < 0 || value > NumberInput.MaxMoney || decimal.Round(value.Value, 2) != value.Value)
        {
            errors.Add(field, $"{label} must be zero or more, with at most two decimals.");
            return 0m;
        }

        return value.Value;
    }

    /// <summary>A required client-made GUID that makes a retried form land once.</summary>
    public static Guid? Key(Guid? value, ValidationErrors errors)
    {
        if (value is null || value == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey is required: a GUID the app makes once per form.");
            return null;
        }

        return value;
    }

    /// <summary>A required note of a review decision (return, reject, adjust, reverse).</summary>
    public static string? Reason(string? value, ValidationErrors errors, string field = "note", string label = "The reason") =>
        Input.RequiredText(value, Data.Configurations.OfficeLengths.Note, errors, field, label);
}
