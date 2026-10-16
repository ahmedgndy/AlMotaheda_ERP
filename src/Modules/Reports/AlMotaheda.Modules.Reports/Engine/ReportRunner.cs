using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Reports.Contracts;

namespace AlMotaheda.Modules.Reports.Engine;

/// <summary>What running a report came to.</summary>
internal sealed record ReportOutcome(
    ReportDefinition? Definition = null,
    ReportData? Data = null,
    IReadOnlyDictionary<string, string>? Filters = null,
    ValidationErrors? Errors = null,
    bool NotFound = false,
    bool Forbidden = false)
{
    public bool Succeeded => Data is not null;
}

/// <summary>Finds the report, checks who may run it, reads its filters and asks the owning module for the rows.</summary>
internal sealed class ReportRunner(ReportCatalog catalog, CompanyClock clock, IUserDirectory users)
{
    /// <summary>Query-string names that belong to the request, not to the report.</summary>
    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "page", "pageSize", "lang" };

    public async Task<ReportOutcome> RunAsync(string code, IReadOnlyDictionary<string, string?> values, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (catalog.Find(code) is not { } entry)
        {
            return new ReportOutcome(NotFound: true);
        }

        if (!ReportCatalog.CanRun(caller, entry.Definition))
        {
            return new ReportOutcome(entry.Definition, Forbidden: true);
        }

        var errors = new ValidationErrors();
        var query = ReportFilters.Parse(entry.Definition, values, clock.Today, errors);
        if (query is null)
        {
            return new ReportOutcome(entry.Definition, Errors: errors);
        }

        var data = await entry.Source.RunAsync(entry.Definition.Code, query, caller, cancellationToken);
        if (data is null)
        {
            return new ReportOutcome(entry.Definition, NotFound: true);
        }

        // Only the filters the report knows, as sent: for the header and the run history.
        var used = entry.Definition.Filters
            .Where(filter => Input.Clean(values.GetValueOrDefault(filter.Name)) is not null)
            .ToDictionary(filter => filter.Name, filter => Input.Clean(values[filter.Name])!);
        return new ReportOutcome(entry.Definition, data, used);
    }

    public async Task<ReportDocument> DocumentAsync(ReportOutcome outcome, string language, int userId, CancellationToken cancellationToken)
    {
        var user = (await users.FindAsync([userId], cancellationToken)).GetValueOrDefault(userId);
        var name = user is null ? $"#{userId}" : language == "ar" ? user.NameAr : user.NameEn;
        return new ReportDocument(outcome.Definition!, language, outcome.Filters!, name, clock.UtcNow, clock.Zone, outcome.Data!);
    }

    public static string Language(string? requested) => string.Equals(requested, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";

    public static int? UserId(ClaimsPrincipal caller) => caller.GetUserId();
}
