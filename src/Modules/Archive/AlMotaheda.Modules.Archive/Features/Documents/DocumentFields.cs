using System.Globalization;
using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Data.Configurations;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary>The metadata a person types about a document, after validation. Shared by upload and edit.</summary>
internal sealed record DocumentFields(string Title, DocumentCategory Category, string? Description, DateOnly? DocumentDate)
{
    /// <summary>Checks every field and reports every failing one at once. <c>null</c> when any field fails.</summary>
    /// <param name="today">The company's today: a document can't be dated in the future.</param>
    public static DocumentFields? Validate(
        string? title, string? category, string? description, string? documentDate, DateOnly today, ValidationErrors errors)
    {
        var cleanTitle = Input.RequiredText(title, DocumentConfiguration.TitleMaxLength, errors, "title", "Title");
        var cleanDescription = Input.OptionalText(description, DocumentConfiguration.DescriptionMaxLength, errors, "description", "Description");
        var parsedCategory = Choice.Required<DocumentCategory>(category, errors, "category");

        var date = Input.OptionalDate(documentDate, errors, "documentDate", "Document date");
        if (date is { } value && value > today)
        {
            errors.Add("documentDate", "Document date can't be in the future.");
        }

        return errors.HasErrors ? null : new DocumentFields(cleanTitle!, parsedCategory!.Value, cleanDescription, date);
    }

    public void ApplyTo(Document document)
    {
        document.Title = Title;
        document.Category = Category;
        document.Description = Description;
        document.DocumentDate = DocumentDate;
    }
}

/// <summary>A link to another module's record, as sent by a client.</summary>
internal sealed record DocumentLinkRequest(string? EntityType, int? EntityId);

/// <summary>Input rules for the links from a document to other modules' records.</summary>
/// <remarks>
/// Only the shape is checked (a known record type and a positive id). Whether that record exists is not: it lives in
/// another module and there is no contract to ask yet (see the README, "Known limits").
/// </remarks>
internal static class LinkInput
{
    /// <summary>More than this many links on one document is a mistake, not a real need.</summary>
    public const int MaxLinks = 20;

    /// <summary>The optional single link of an upload form: <c>entityType</c> and <c>entityId</c>, both or neither.</summary>
    public static (LinkedEntityType Type, int Id)? ValidateOne(string? entityType, string? entityId, ValidationErrors errors)
    {
        var typeText = Input.Clean(entityType);
        var idText = Input.Clean(entityId);
        if (typeText is null && idText is null)
        {
            return null;
        }

        if (typeText is null)
        {
            errors.Add("entityType", "entityType is required when entityId is sent.");
        }

        int? id = null;
        if (idText is null)
        {
            errors.Add("entityId", "entityId is required when entityType is sent.");
        }
        else if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            errors.Add("entityId", "entityId must be a positive whole number.");
        }
        else
        {
            id = parsed;
        }

        var type = typeText is null ? null : Choice.Required<LinkedEntityType>(typeText, errors, "entityType");
        return type is { } t && id is { } i ? (t, i) : null;
    }

    /// <summary>The whole list of a links replacement. An empty list removes every link.</summary>
    public static IReadOnlyList<(LinkedEntityType Type, int Id)>? ValidateList(IReadOnlyList<DocumentLinkRequest?>? links, ValidationErrors errors)
    {
        if (links is null)
        {
            errors.Add("links", "links is required. Send an empty list to remove every link.");
            return null;
        }

        if (links.Count > MaxLinks)
        {
            errors.Add("links", $"A document can have at most {MaxLinks} links.");
            return null;
        }

        var result = new List<(LinkedEntityType Type, int Id)>(links.Count);
        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];
            var prefix = $"links[{index}]";
            var type = Choice.Required<LinkedEntityType>(link?.EntityType, errors, $"{prefix}.entityType");
            if (link?.EntityId is not ({ } id and >= 1))
            {
                errors.Add($"{prefix}.entityId", "entityId must be a positive whole number.");
                continue;
            }

            if (type is null)
            {
                continue;
            }

            if (result.Contains((type.Value, id)))
            {
                errors.Add(prefix, "This record is listed twice.");
                continue;
            }

            result.Add((type.Value, id));
        }

        return errors.HasErrors ? null : result;
    }
}

/// <summary>Reads enum names sent by clients, ignoring case, with the list of allowed names in the error.</summary>
internal static class Choice
{
    public static TEnum? Required<TEnum>(string? value, ValidationErrors errors, string field)
        where TEnum : struct, Enum
    {
        if (Input.Clean(value) is null)
        {
            errors.Add(field, $"{field} is required.");
            return null;
        }

        return Optional<TEnum>(value, errors, field);
    }

    public static TEnum? Optional<TEnum>(string? value, ValidationErrors errors, string field)
        where TEnum : struct, Enum
    {
        if (Input.Clean(value) is null)
        {
            return null;
        }

        if (Input.TryParseName<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        errors.Add(field, $"{field} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return null;
    }
}
