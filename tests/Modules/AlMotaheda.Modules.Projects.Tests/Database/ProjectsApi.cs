using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Projects.Tests.Database;

/// <summary>
/// The real API on a fresh SQL Server database (Access and Projects migrations applied at startup, first Executive
/// created), with helpers that create users and projects the way the frontend will.
/// </summary>
internal sealed class ProjectsApi : IAsyncDisposable
{
    private ProjectsApi(AccessApi access) => Access = access;

    public AccessApi Access { get; }

    public static async Task<ProjectsApi> StartAsync(SqlServerFixture database) => new(await AccessApi.StartAsync(database));

    public Task<Session> SignInExecutiveAsync() => Access.SignInExecutiveAsync();

    /// <summary>A new user with their own password set, signed in. Returns the user's id and session.</summary>
    public async Task<(int Id, Session Session)> CreateSignedInUserAsync(Session executive, string role)
    {
        var (user, session) = await Access.CreateSignedInUserAsync(executive, role);
        return (user.GetProperty("id").GetInt32(), session);
    }

    public async Task<int> CreateUserAsync(Session executive, string role) =>
        (await Access.CreateUserAsync(executive, role)).GetProperty("id").GetInt32();

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Session session, object? body = null) =>
        Access.SendAsync(method, path, session, body);

    /// <summary>The body of a valid project form; <paramref name="changes"/> replaces or adds fields.</summary>
    public static Dictionary<string, object?> ProjectBody(string code, int managerUserId, Dictionary<string, object?>? changes = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["nameEn"] = $"Project {code}",
            ["nameAr"] = $"مشروع {code}",
            ["clientNameEn"] = "Badr Urban Development Co.",
            ["locationEn"] = "Badr City, Cairo",
            ["siteLatitude"] = 30.1395m,
            ["siteLongitude"] = 31.7085m,
            ["siteRadiusMeters"] = 500,
            ["startDate"] = "2025-03-01",
            ["endDate"] = "2026-12-31",
            ["status"] = "Active",
            ["managerUserId"] = managerUserId,
        };

        foreach (var (key, value) in changes ?? [])
        {
            body[key] = value;
        }

        return body;
    }

    /// <summary>An Executive creates a project; returns its JSON.</summary>
    public async Task<JsonElement> CreateProjectAsync(
        Session executive, string code, int managerUserId, int[]? siteEngineers = null, int[]? team = null)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/projects", executive, ProjectBody(code, managerUserId, new()
        {
            ["siteEngineerUserIds"] = siteEngineers ?? [],
            ["teamUserIds"] = team ?? [],
        }));
        await AccessApi.EnsureStatus(response, HttpStatusCode.Created);
        return await AccessApi.Json(response);
    }

    /// <summary>The body for <c>PUT /api/projects/{id}</c> from the project as last read, with <paramref name="changes"/>.</summary>
    public static Dictionary<string, object?> EditBody(JsonElement project, Dictionary<string, object?>? changes = null)
    {
        var body = new Dictionary<string, object?>();
        foreach (var name in new[]
                 {
                     "code", "nameAr", "nameEn", "clientNameAr", "clientNameEn", "locationAr", "locationEn", "contractNo",
                     "addressAr", "addressEn", "siteLatitude", "siteLongitude", "siteRadiusMeters", "startDate", "endDate",
                     "status", "notes", "rowVersion",
                 })
        {
            body[name] = project.GetProperty(name).Clone();
        }

        foreach (var (key, value) in changes ?? [])
        {
            body[key] = value;
        }

        return body;
    }

    public async Task<JsonElement> GetProjectAsync(Session session, int projectId)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}", session);
        await AccessApi.EnsureStatus(response, HttpStatusCode.OK);
        return await AccessApi.Json(response);
    }

    public async Task<List<JsonElement>> MembersAsync(Session session, int projectId, bool includeEnded = false)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/members?includeEnded={includeEnded}", session);
        await AccessApi.EnsureStatus(response, HttpStatusCode.OK);
        return (await AccessApi.Json(response)).GetProperty("items").EnumerateArray().ToList();
    }

    /// <summary>The codes of the projects <paramref name="session"/> sees, in code order.</summary>
    public async Task<List<string>> VisibleCodesAsync(Session session, string query = "")
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/projects?pageSize=100{query}", session);
        await AccessApi.EnsureStatus(response, HttpStatusCode.OK);
        return (await AccessApi.Json(response)).GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("code").GetString()!)
            .ToList();
    }

    public async Task<T> QueryAsync<T>(Func<ProjectsDbContext, Task<T>> query)
    {
        await using var scope = Access.App.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ProjectsDbContext>());
    }

    public ValueTask DisposeAsync() => Access.DisposeAsync();
}
