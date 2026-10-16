using System.Globalization;
using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Reports.Contracts;

namespace AlMotaheda.Modules.Reports.Engine;

/// <summary>Every report every module offers, by code, and who may run each.</summary>
internal sealed class ReportCatalog
{
    private readonly Dictionary<string, (IReportSource Source, ReportDefinition Definition)> _reports;

    public ReportCatalog(IEnumerable<IReportSource> sources)
    {
        _reports = new Dictionary<string, (IReportSource, ReportDefinition)>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            foreach (var definition in source.Reports)
            {
                if (!_reports.TryAdd(definition.Code, (source, definition)))
                {
                    throw new InvalidOperationException($"Two modules offer report {definition.Code}.");
                }
            }
        }
    }

    public IEnumerable<ReportDefinition> All => _reports.Values.Select(entry => entry.Definition).OrderBy(definition => definition.Code);

    public (IReportSource Source, ReportDefinition Definition)? Find(string code) => _reports.TryGetValue(code, out var entry) ? entry : null;

    public static bool CanRun(ClaimsPrincipal caller, ReportDefinition definition) =>
        definition.AnyOf.Any(permission => PermissionActions.TryParse(permission.Action, out var action) && caller.HasPermission(permission.Module, action));
}

/// <summary>Reads a report's filters from the query string, against its definition, reporting every problem at once.</summary>
internal static class ReportFilters
{
    public static ReportQuery? Parse(ReportDefinition definition, IReadOnlyDictionary<string, string?> values, DateOnly today, ValidationErrors errors)
    {
        var parsed = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var filter in definition.Filters)
        {
            var text = Input.Clean(values.GetValueOrDefault(filter.Name));
            if (text is null)
            {
                if (filter.Required)
                {
                    errors.Add(filter.Name, $"{filter.LabelEn} is required.");
                }

                continue;
            }

            switch (filter.Type)
            {
                case ReportFilterTypes.Date when DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                    parsed[filter.Name] = date;
                    break;
                case ReportFilterTypes.Month when DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month):
                    parsed[filter.Name] = month;
                    break;
                case ReportFilterTypes.Id when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0:
                    parsed[filter.Name] = id;
                    break;
                case ReportFilterTypes.Option when filter.Options is { } options && options.FirstOrDefault(option => string.Equals(option, text, StringComparison.OrdinalIgnoreCase)) is { } option:
                    parsed[filter.Name] = option;
                    break;
                default:
                    errors.Add(filter.Name, filter.Type switch
                    {
                        ReportFilterTypes.Date => $"{filter.Name} must be a date written as yyyy-MM-dd.",
                        ReportFilterTypes.Month => $"{filter.Name} must be a month written as yyyy-MM.",
                        ReportFilterTypes.Id => $"{filter.Name} must be a record id.",
                        _ => $"{filter.Name} must be one of: {string.Join(", ", filter.Options ?? [])}.",
                    });
                    break;
            }
        }

        if (parsed.TryGetValue("from", out var from) && parsed.TryGetValue("to", out var to) && (DateOnly)to < (DateOnly)from)
        {
            errors.Add("to", "to can't be before from.");
        }

        return errors.HasErrors ? null : new ReportQuery(parsed, today);
    }
}

/// <summary>The totals row: the sum of every column marked <see cref="ReportColumn.Total"/>.</summary>
internal static class ReportTotals
{
    public static IReadOnlyDictionary<string, object?> Of(ReportDefinition definition, IEnumerable<ReportRow> rows)
    {
        var list = rows.ToList();
        return definition.Columns.Where(column => column.Total).ToDictionary(
            column => column.Key,
            column => (object?)list.Sum(row => row.Cells.GetValueOrDefault(column.Key) switch
            {
                decimal amount => amount,
                int count => count,
                _ => 0m,
            }));
    }
}
