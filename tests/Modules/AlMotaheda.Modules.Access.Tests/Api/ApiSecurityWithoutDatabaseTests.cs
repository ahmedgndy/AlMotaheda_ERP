using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlMotaheda.Api.Tests;

namespace AlMotaheda.Modules.Access.Tests.Api;

/// <summary>
/// The real API over HTTP, for checks that stop a request before it reaches the database:
/// authentication, permission and Executive checks, validation and rate limiting.
/// </summary>
public sealed class ApiSecurityWithoutDatabaseTests
{
    [Theory]
    [InlineData("GET", "/api/users")]
    [InlineData("GET", "/api/users/1")]
    [InlineData("POST", "/api/users")]
    [InlineData("GET", "/api/roles")]
    [InlineData("GET", "/api/me")]
    [InlineData("POST", "/api/auth/change-password")]
    [InlineData("PUT", "/api/users/1/permissions/projects")]
    public async Task Requests_without_a_token_get_401_with_code(string method, string path)
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : JsonContent.Create(new { }),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("unauthenticated", await CodeOf(response));
    }

    [Fact]
    public async Task Tampered_token_gets_401()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("accounts", "VCEAF")]);
        var tampered = token[..^4] + (token[^4..] == "AAAA" ? "BBBB" : "AAAA");

        var response = await Send(app, HttpMethod.Get, "/api/users", tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Listing_users_without_accounts_view_is_forbidden()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "manager", permissions: [("projects", "VCEF")]);

        var response = await Send(app, HttpMethod.Get, "/api/users", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Fact]
    public async Task Reading_another_user_without_accounts_view_is_forbidden_before_any_lookup()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(userId: 7, role: "engineer");

        var response = await Send(app, HttpMethod.Get, "/api/users/8", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/users")]
    [InlineData("PUT", "/api/users/5")]
    [InlineData("POST", "/api/users/5/disable")]
    [InlineData("POST", "/api/users/5/enable")]
    [InlineData("POST", "/api/users/5/reset-password")]
    [InlineData("PUT", "/api/users/5/permissions/accounts")]
    [InlineData("POST", "/api/users/5/permissions/reset")]
    [InlineData("PUT", "/api/roles/manager/permissions")]
    public async Task User_management_is_refused_to_non_executives_even_with_every_accounts_permission(string method, string path)
    {
        // An accountant who was somehow given full accounts permissions still can't manage users or permissions:
        // the rule is the Executive role, not a permission anyone can be granted.
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(userId: 5, role: "accountant", permissions: [("accounts", "VCEAF"), ("settings", "VCEAF")]);

        var response = await Send(app, new HttpMethod(method), path, token, new
        {
            email = "x@y.com", nameAr = "س", nameEn = "X", role = "executive", initialPassword = "long enough password",
            actions = new[] { "View", "Create", "Edit" }, rowVersion = "AAAAAAAAB9E=",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Pending_password_change_blocks_everything_except_the_allowed_endpoints()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "accountant", mustChangePassword: true, permissions: [("accounts", "V")]);

        var blocked = await Send(app, HttpMethod.Get, "/api/users", token);
        var allowed = await Send(app, HttpMethod.Post, "/api/auth/change-password", token, new { currentPassword = "", newPassword = "" });

        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("password_change_required", await CodeOf(blocked));

        // Reaches the endpoint (validation runs after the user lookup, which fails here without a database);
        // what matters is that authorization let it through.
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);
    }

    [Fact]
    public async Task Login_validates_input_before_touching_the_database()
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/auth/login", new { email = " ", password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", body.RootElement.GetProperty("code").GetString());
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("email", out _));
        Assert.True(body.RootElement.GetProperty("errors").TryGetProperty("password", out _));
        Assert.Matches("^[0-9a-f]{32}$", body.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task Sign_in_is_rate_limited_per_client_address()
    {
        await using var app = await TestApplication.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:Authentication:PermitLimit"] = "3",
        });

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            statuses.Add((await app.Client.PostAsJsonAsync("/api/auth/login", new { email = "", password = "" })).StatusCode);
        }

        var limited = await app.Client.PostAsJsonAsync("/api/auth/login", new { email = "", password = "" });
        var refreshStillAllowed = await app.Client.PostAsync("/api/auth/refresh", content: null);

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests], statuses);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal("too_many_requests", await CodeOf(limited));

        // Refresh has its own, larger budget: pages keep working while sign-in is throttled.
        Assert.Equal(HttpStatusCode.Unauthorized, refreshStillAllowed.StatusCode);
    }

    [Fact]
    public async Task Token_refresh_has_its_own_rate_limit()
    {
        await using var app = await TestApplication.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["RateLimiting:TokenRefresh:PermitLimit"] = "2",
        });

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            statuses.Add((await app.Client.PostAsync("/api/auth/refresh", content: null)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Page_numbers_beyond_the_limit_are_rejected_instead_of_failing()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "accountant", permissions: [("accounts", "V")]);

        var response = await Send(app, HttpMethod.Get, "/api/users?page=2147483647&pageSize=100", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
    }

    [Theory]
    [InlineData("Create,Edit")]
    [InlineData("1")]
    [InlineData("Delete")]
    public async Task Action_names_must_match_exactly(string action)
    {
        // "Create,Edit" would otherwise be read as Approve by .NET's enum parser.
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("accounts", "VCEAF")]);

        var response = await Send(app, HttpMethod.Put, "/api/users/5/permissions/projects", token,
            new { actions = new[] { action }, rowVersion = "AAAAAAAAB9E=" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_refresh_cookie_is_treated_as_signed_out()
    {
        await using var app = await TestApplication.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", "__Secure-erp-refresh=not*a*valid*token");

        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_without_a_cookie_ends_the_session_and_clears_the_cookie()
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.PostAsync("/api/auth/refresh", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await CodeOf(response));
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("__Secure-erp-refresh=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logout_always_returns_204_and_clears_the_cookie()
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("path=/api/auth", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Swagger_offers_bearer_authorization_and_lists_the_access_endpoints()
    {
        await using var app = await TestApplication.StartAsync(environment: "Development");

        var document = await app.Client.GetStringAsync("/swagger/v1/swagger.json");

        Assert.Contains("\"bearer\"", document);
        foreach (var path in new[] { "/api/auth/login", "/api/auth/refresh", "/api/auth/logout", "/api/auth/change-password", "/api/me",
                     "/api/users", "/api/users/{id}", "/api/users/{id}/disable", "/api/users/{id}/enable", "/api/users/{id}/reset-password",
                     "/api/users/{id}/permissions", "/api/users/{id}/permissions/{module}", "/api/users/{id}/permissions/reset",
                     "/api/roles", "/api/roles/{code}/permissions" })
        {
            Assert.Contains($"\"{path}\"", document);
        }
    }

    [Fact]
    public async Task Health_includes_a_database_check_when_a_database_is_configured()
    {
        await using var app = await TestApplication.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Erp"] = "Server=127.0.0.1,9;Database=Unreachable;User Id=x;Password=x;Connect Timeout=1;TrustServerCertificate=true",
        });

        var response = await app.Client.GetAsync("/health");
        var liveness = await app.Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Other checks (the archive storage folder) may be listed too; the database one must be there and failing.
        var check = Assert.Single(
            body.RootElement.GetProperty("checks").EnumerateArray(), entry => entry.GetProperty("name").GetString() == "database");
        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    private static async Task<HttpResponseMessage> Send(
        TestApplication app, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await app.Client.SendAsync(request);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
