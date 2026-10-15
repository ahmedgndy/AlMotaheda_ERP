using System.Globalization;

namespace AlMotaheda.BuildingBlocks.Validation;

/// <summary>Collects field errors for a 400 response. Field names match the JSON property names.</summary>
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    /// <summary>How many fields have errors; lets a nested form tell whether it added any.</summary>
    public int FieldCount => _errors.Count;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            _errors[field] = messages = [];
        }

        messages.Add(message);
    }

    public bool Has(string field) => _errors.ContainsKey(field);

    public IDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal);
}

/// <summary>Small input rules shared by the slices. The server is the authority; the frontend may repeat them.</summary>
public static class Input
{
    /// <summary>Trims text and turns empty text into <c>null</c>.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? RequiredText(string? value, int maxLength, ValidationErrors errors, string field, string label)
    {
        var text = Clean(value);
        if (text is null)
        {
            errors.Add(field, $"{label} is required.");
        }
        else if (text.Length > maxLength)
        {
            errors.Add(field, $"{label} must be at most {maxLength} characters.");
        }

        return text;
    }

    public static string? OptionalText(string? value, int maxLength, ValidationErrors errors, string field, string label)
    {
        var text = Clean(value);
        if (text is not null && text.Length > maxLength)
        {
            errors.Add(field, $"{label} must be at most {maxLength} characters.");
        }

        return text;
    }

    /// <summary>A required date in <c>yyyy-MM-dd</c> form.</summary>
    public static DateOnly? RequiredDate(string? value, ValidationErrors errors, string field, string label)
    {
        var text = Clean(value);
        if (text is null)
        {
            errors.Add(field, $"{label} is required.");
            return null;
        }

        return ParseDate(text, errors, field, label);
    }

    /// <summary>An optional date in <c>yyyy-MM-dd</c> form; <c>null</c> when empty.</summary>
    public static DateOnly? OptionalDate(string? value, ValidationErrors errors, string field, string label)
    {
        var text = Clean(value);
        return text is null ? null : ParseDate(text, errors, field, label);
    }

    /// <summary>
    /// Reads an enum by its name, ignoring case ("onHold", "OnHold" and "onhold" all work). Numbers are refused,
    /// because <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would accept any number.
    /// </summary>
    public static bool TryParseName<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        var text = Clean(value);
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
            {
                result = Enum.Parse<TEnum>(name);
                return true;
            }
        }

        result = default;
        return false;
    }

    /// <summary>
    /// An optional enum filter or field: <c>null</c> when empty, otherwise one of the names (any letter case), and
    /// a field error listing the names when it is none of them.
    /// </summary>
    public static TEnum? OptionalName<TEnum>(string? value, ValidationErrors errors, string field)
        where TEnum : struct, Enum
    {
        if (Clean(value) is not { } text)
        {
            return null;
        }

        if (TryParseName<TEnum>(text, out var parsed))
        {
            return parsed;
        }

        errors.Add(field, $"{field} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return null;
    }

    /// <summary>A required enum field: one of the names (any letter case).</summary>
    public static TEnum? RequiredName<TEnum>(string? value, ValidationErrors errors, string field)
        where TEnum : struct, Enum
    {
        if (TryParseName<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        errors.Add(field, $"{field} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return null;
    }

    private static DateOnly? ParseDate(string text, ValidationErrors errors, string field, string label)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors.Add(field, $"{label} must be a date in the form yyyy-MM-dd.");
        return null;
    }
}
