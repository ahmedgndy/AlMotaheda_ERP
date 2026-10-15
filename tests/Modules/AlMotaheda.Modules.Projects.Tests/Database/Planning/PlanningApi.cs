using System.Globalization;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Planning;

/// <summary>
/// Calls the budget, catalog, daily report, report request and overview endpoints the way the frontend will, on top
/// of <see cref="ProjectsApi"/>. Every helper asserts the expected status, so a test reads as the business flow.
/// </summary>
internal static class PlanningApi
{
    /// <summary>Today at the company, near enough for dates the server checks against its own "today".</summary>
    public static DateOnly CompanyToday => DateOnly.FromDateTime(DateTime.UtcNow);

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static async Task<JsonElement> ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        await EnsureStatus(response, status);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> PostAsync(this ProjectsApi api, Session session, string path, object? body = null) =>
        api.SendAsync(HttpMethod.Post, path, session, body ?? new { });

    public static Task<HttpResponseMessage> PutAsync(this ProjectsApi api, Session session, string path, object body) =>
        api.SendAsync(HttpMethod.Put, path, session, body);

    public static async Task<JsonElement> GetOkAsync(this ProjectsApi api, Session session, string path) =>
        await ExpectAsync(await api.SendAsync(HttpMethod.Get, path, session), HttpStatusCode.OK);

    public static string RowVersion(this JsonElement element) => element.GetProperty("rowVersion").GetString()!;

    public static string Status(this JsonElement element) => element.GetProperty("status").GetString()!;

    public static JsonElement Group(this JsonElement groups, string costGroup) =>
        groups.EnumerateArray().Single(group => group.GetProperty("costGroup").GetString() == costGroup);
}
