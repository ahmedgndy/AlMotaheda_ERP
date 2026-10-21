using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Tests.Unit;
using AlMotaheda.Modules.Projects.Tests.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Archive.Tests.Database;

/// <summary>
/// The real API on a fresh SQL Server database (Access, Projects and Archive migrations applied at startup) with its
/// own storage folder, and helpers that upload and read documents the way the frontend will.
/// </summary>
internal sealed class ArchiveApi : IAsyncDisposable
{
    private readonly TemporaryFolder _storage;

    private ArchiveApi(AccessApi access, TemporaryFolder storage)
    {
        Access = access;
        _storage = storage;
    }

    public AccessApi Access { get; }

    /// <summary>Every file in the storage folder, at any depth.</summary>
    public IReadOnlyList<string> StoredFiles => _storage.Files();

    public static async Task<ArchiveApi> StartAsync(SqlServerFixture database, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var storage = new TemporaryFolder();
        var access = await AccessApi.StartAsync(
            database,
            new Dictionary<string, string?> { ["Archive:Storage:RootPath"] = storage.Path },
            configureBuilder);
        return new ArchiveApi(access, storage);
    }

    public Task<Session> SignInExecutiveAsync() => Access.SignInExecutiveAsync();

    public async Task<(int Id, Session Session)> CreateSignedInUserAsync(Session executive, string role)
    {
        var (user, session) = await Access.CreateSignedInUserAsync(executive, role);
        return (user.GetProperty("id").GetInt32(), session);
    }

    /// <summary>An Executive creates a project with a manager and a team; returns its id.</summary>
    public async Task<int> CreateProjectAsync(Session executive, string code, int managerUserId, int[]? siteEngineers = null, int[]? team = null)
    {
        var response = await Access.SendAsync(HttpMethod.Post, "/api/projects", executive, ProjectsApi.ProjectBody(code, managerUserId, new()
        {
            ["siteEngineerUserIds"] = siteEngineers ?? [],
            ["teamUserIds"] = team ?? [],
        }));
        await AccessApi.EnsureStatus(response, HttpStatusCode.Created);
        return (await AccessApi.Json(response)).GetProperty("id").GetInt32();
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Session session, object? body = null) =>
        Access.SendAsync(method, path, session, body);

    /// <summary>
    /// Files a daily report for today (UTC date, never ahead of the company's date) and returns its id: a real
    /// record of the project for document links. One report per author, project and day.
    /// </summary>
    public async Task<int> CreateDailyReportAsync(Session author, int projectId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var response = await Access.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/daily-reports", author, new { reportDate = today, issues = "Site visit" });
        await AccessApi.EnsureStatus(response, HttpStatusCode.Created);
        return (await AccessApi.Json(response)).GetProperty("id").GetInt32();
    }

    /// <summary>Posts an upload form; <paramref name="fields"/> replaces the default title and category.</summary>
    public Task<HttpResponseMessage> PostUploadAsync(
        Session session, int projectId, string fileName, byte[] bytes, Dictionary<string, string>? fields = null, string? idempotencyKey = null)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in fields ?? new Dictionary<string, string> { ["title"] = "Payment certificate 7", ["category"] = "PaymentCertificate" })
        {
            form.Add(new StringContent(value), name);
        }

        form.Add(new ByteArrayContent(bytes), "file", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return Access.App.Client.SendAsync(request);
    }

    /// <summary>Uploads a file and returns the new document's JSON (asserts 201).</summary>
    public async Task<JsonElement> UploadAsync(
        Session session, int projectId, string fileName, byte[] bytes, Dictionary<string, string>? fields = null)
    {
        var response = await PostUploadAsync(session, projectId, fileName, bytes, fields);
        await AccessApi.EnsureStatus(response, HttpStatusCode.Created);
        return await AccessApi.Json(response);
    }

    public Task<HttpResponseMessage> GetContentAsync(Session session, int projectId, int documentId, string? query = null, Action<HttpRequestMessage>? change = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{projectId}/documents/{documentId}/content{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        change?.Invoke(request);
        return Access.App.Client.SendAsync(request);
    }

    public async Task<JsonElement> ListAsync(Session session, int projectId, string query = "")
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/documents?pageSize=100{query}", session);
        await AccessApi.EnsureStatus(response, HttpStatusCode.OK);
        return await AccessApi.Json(response);
    }

    public async Task<T> QueryAsync<T>(Func<ArchiveDbContext, Task<T>> query)
    {
        await using var scope = Access.App.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<ArchiveDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await Access.DisposeAsync();
        _storage.Dispose();
    }
}
