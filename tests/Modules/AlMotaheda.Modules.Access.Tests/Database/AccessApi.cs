using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Data;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Access.Tests.Database;

/// <summary>
/// The real API on a fresh SQL Server database: migrations applied and the first Executive created at startup,
/// exactly as on a new server. Helpers sign in and call endpoints the way the frontend will.
/// </summary>
internal sealed class AccessApi : IAsyncDisposable
{
    public const string ExecutiveEmail = "executive@almotaheda.test";
    public const string BootstrapPassword = "first-start-password";
    public const string ExecutivePassword = "executive-own-password";
    public const string UserPassword = "a-user-first-password";

    private const string RefreshCookieName = "__Secure-erp-refresh";

    private AccessApi(TestApplication app) => App = app;

    public TestApplication App { get; }

    public static async Task<AccessApi> StartAsync(
        SqlServerFixture database,
        IDictionary<string, string?>? settings = null,
        Action<Microsoft.AspNetCore.Builder.WebApplicationBuilder>? configureBuilder = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Erp"] = database.NewDatabaseConnectionString(),
            ["Database:ApplyMigrationsOnStartup"] = "true",
            ["Access:Bootstrap:Email"] = ExecutiveEmail,
            ["Access:Bootstrap:Password"] = BootstrapPassword,

            // Tests sign in many times a minute from one address.
            ["RateLimiting:Authentication:PermitLimit"] = "10000",
        };

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        return new AccessApi(await TestApplication.StartAsync(configuration: configuration, configureBuilder: configureBuilder));
    }

    public Task<HttpResponseMessage> PostLoginAsync(string email, string password, bool rememberMe = false) =>
        App.Client.PostAsJsonAsync("/api/auth/login", new { email, password, rememberMe });

    public async Task<Session> LoginAsync(string email, string password, bool rememberMe = false)
    {
        var response = await PostLoginAsync(email, password, rememberMe);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Session.FromAsync(response);
    }

    /// <summary>Signs in the bootstrap Executive and completes the forced password change.</summary>
    private Session? _executive;

    /// <summary>
    /// The bootstrap Executive, signed in with their own password. Signed in once per test host and kept: the
    /// bootstrap password works only until the first change, and later steps (approving payments) need the session.
    /// </summary>
    public async Task<Session> SignInExecutiveAsync() => _executive ??= await SignInExecutiveOnceAsync();

    private async Task<Session> SignInExecutiveOnceAsync()
    {
        var first = await LoginAsync(ExecutiveEmail, BootstrapPassword);
        if (!first.MustChangePassword)
        {
            return first;
        }

        var changed = await SendAsync(HttpMethod.Post, "/api/auth/change-password", first,
            new { currentPassword = BootstrapPassword, newPassword = ExecutivePassword });
        await EnsureStatus(changed, HttpStatusCode.OK);
        return await Session.FromAsync(changed);
    }

    /// <summary>An Executive creates a user; returns the new user's JSON.</summary>
    public async Task<JsonElement> CreateUserAsync(Session executive, string role, string? email = null, string? phone = null)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/users", executive, new
        {
            email = email ?? $"{role}.{Guid.NewGuid():N}@almotaheda.test",
            phone,
            nameAr = "مستخدم تجريبي",
            nameEn = "Test User",
            jobTitleEn = "Tester",
            role,
            initialPassword = UserPassword,
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    /// <summary>Creates a user and signs them in with their own password set (no pending change).</summary>
    public async Task<(JsonElement User, Session Session)> CreateSignedInUserAsync(Session executive, string role)
    {
        var user = await CreateUserAsync(executive, role);
        var email = user.GetProperty("email").GetString()!;
        var first = await LoginAsync(email, UserPassword);
        var changed = await SendAsync(HttpMethod.Post, "/api/auth/change-password", first,
            new { currentPassword = UserPassword, newPassword = UserPassword + "-changed" });
        await EnsureStatus(changed, HttpStatusCode.OK);
        return (user, await Session.FromAsync(changed));
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Session? session, object? body = null) =>
        SendAsync(method, path, session?.AccessToken, body, session?.RefreshCookie);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? accessToken, object? body = null, string? refreshCookie = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        // The test server runs plain HTTP, where HttpClient won't send a Secure cookie by itself.
        if (refreshCookie is not null)
        {
            request.Headers.Add("Cookie", $"{RefreshCookieName}={refreshCookie}");
        }

        return App.Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> RefreshAsync(string? refreshCookie) =>
        SendAsync(HttpMethod.Post, "/api/auth/refresh", accessToken: null, body: null, refreshCookie);

    /// <summary>Runs a query against the test database with the module's own context.</summary>
    public async Task<T> QueryAsync<T>(Func<AccessDbContext, Task<T>> query)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AccessDbContext>());
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await Json(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    public static async Task EnsureStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            Assert.Fail($"Expected {(int)expected} but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    public static string? RefreshCookieOf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return null;
        }

        var cookie = cookies.FirstOrDefault(value => value.StartsWith(RefreshCookieName + "=", StringComparison.Ordinal));
        var value = cookie?[(RefreshCookieName.Length + 1)..].Split(';')[0];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}

/// <summary>What the frontend keeps after sign-in: the access token in memory, the cookie in the browser.</summary>
internal sealed record Session(string AccessToken, string? RefreshCookie, bool MustChangePassword, JsonElement Body)
{
    public int UserId => Body.GetProperty("user").GetProperty("id").GetInt32();

    public static async Task<Session> FromAsync(HttpResponseMessage response)
    {
        var body = await AccessApi.Json(response);
        return new Session(
            body.GetProperty("accessToken").GetString()!,
            AccessApi.RefreshCookieOf(response),
            body.GetProperty("mustChangePassword").GetBoolean(),
            body);
    }
}
