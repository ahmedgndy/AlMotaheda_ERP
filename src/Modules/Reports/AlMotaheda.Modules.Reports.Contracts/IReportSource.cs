using System.Security.Claims;

namespace AlMotaheda.Modules.Reports.Contracts;

/// <summary>
/// A module's reports. The Reports module checks the permission, validates the filters against
/// <see cref="ReportDefinition.Filters"/>, pages, totals, exports and prints; the source computes the rows with its
/// own queries and applies its own data scope (which projects the caller may see).
/// </summary>
public interface IReportSource
{
    IReadOnlyList<ReportDefinition> Reports { get; }

    /// <summary>
    /// Every row of report <paramref name="code"/> for the filters (never paged). <c>null</c> when a project filter
    /// names a project the caller may not see: the Reports module answers 404, as everywhere.
    /// </summary>
    Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken);
}

/// <param name="Code">Unique, for example <c>FIN-LEDGER</c>.</param>
/// <param name="Category">One of <see cref="ReportCategories"/>.</param>
/// <param name="AnyOf">The caller needs at least one of these permissions (module and action name, for example <c>finance</c>, <c>SeeMoney</c>).</param>
public sealed record ReportDefinition(
    string Code,
    string NameAr,
    string NameEn,
    string Category,
    IReadOnlyList<ReportPermission> AnyOf,
    IReadOnlyList<ReportFilter> Filters,
    IReadOnlyList<ReportColumn> Columns);

public sealed record ReportPermission(string Module, string Action);

/// <param name="Type">One of <see cref="ReportFilterTypes"/>.</param>
/// <param name="Options">For <see cref="ReportFilterTypes.Option"/>: the allowed values.</param>
public sealed record ReportFilter(string Name, string Type, bool Required, string LabelAr, string LabelEn, IReadOnlyList<string>? Options = null);

/// <param name="Type">One of <see cref="ReportColumnTypes"/>.</param>
/// <param name="Total">Summed into the totals row.</param>
public sealed record ReportColumn(string Key, string TitleAr, string TitleEn, string Type, bool Total = false);

/// <summary>The validated filter values: dates as <see cref="DateOnly"/>, ids as <see cref="int"/>, the rest as text.</summary>
public sealed record ReportQuery(IReadOnlyDictionary<string, object> Values, DateOnly Today)
{
    public DateOnly? Date(string name) => Values.TryGetValue(name, out var value) ? (DateOnly)value : null;

    public int? Id(string name) => Values.TryGetValue(name, out var value) ? (int)value : null;

    public string? Text(string name) => Values.TryGetValue(name, out var value) ? (string)value : null;
}

/// <param name="Note">Shown under the report, for example what the data doesn't cover yet.</param>
public sealed record ReportData(IReadOnlyList<ReportRow> Rows, string? Note = null);

/// <param name="Cells">By column key: text, <see cref="DateOnly"/>, <see cref="decimal"/>, <see cref="int"/> or <c>null</c>.</param>
/// <param name="Link">Where the row drills down to.</param>
public sealed record ReportRow(IReadOnlyDictionary<string, object?> Cells, ReportLink? Link = null);

/// <param name="Kind">What to open, for example <c>project</c>, <c>paymentRequest</c>, <c>report</c>.</param>
/// <param name="Id">The record, or <c>null</c> for a report link.</param>
/// <param name="Report">For a drill-down to another report: its code; the filters are in <paramref name="Filters"/>.</param>
public sealed record ReportLink(string Kind, int? Id = null, int? ProjectId = null, string? Report = null, IReadOnlyDictionary<string, string>? Filters = null);

public static class ReportCategories
{
    public const string Finance = "Finance";
    public const string Projects = "Projects";
    public const string Parties = "Parties";
    public const string Labor = "Labor";
    public const string Equipment = "Equipment";
    public const string HumanResources = "HumanResources";
}

public static class ReportFilterTypes
{
    /// <summary><c>yyyy-MM-dd</c>.</summary>
    public const string Date = "Date";

    /// <summary><c>yyyy-MM</c>, passed as the month's first day.</summary>
    public const string Month = "Month";

    /// <summary>A record id: project, supplier, worker…</summary>
    public const string Id = "Id";

    /// <summary>One of <see cref="ReportFilter.Options"/>.</summary>
    public const string Option = "Option";
}

public static class ReportColumnTypes
{
    public const string Text = "Text";
    public const string Date = "Date";
    public const string Money = "Money";
    public const string Quantity = "Quantity";
    public const string Integer = "Integer";
}

/// <summary>The permission action names, as <c>ReportPermission.Action</c> takes them.</summary>
public static class ReportActions
{
    public const string View = "View";
    public const string SeeMoney = "SeeMoney";
}
