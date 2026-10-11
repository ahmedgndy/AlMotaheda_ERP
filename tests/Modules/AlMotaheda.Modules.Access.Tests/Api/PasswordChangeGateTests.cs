using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Access.Tests.Api;

/// <summary>
/// A user who must change their password can only change it, read their own profile and sign out.
/// Checked against every /api endpoint the host maps, so endpoints added later are covered too.
/// </summary>
public sealed class PasswordChangeGateTests
{
    private static readonly HashSet<string> AllowedDuringPasswordChange =
    [
        "POST /api/auth/login",
        "POST /api/auth/refresh",
        "POST /api/auth/logout",
        "POST /api/auth/change-password",
        "GET /api/me",
    ];

    [Fact]
    public async Task Every_other_api_endpoint_refuses_a_user_who_must_change_their_password()
    {
        await using var app = await TestApplication.StartAsync();

        // The most powerful token possible, apart from the pending password change.
        var token = TestTokens.Create(
            role: RoleCodes.Executive,
            mustChangePassword: true,
            permissions: PermissionCatalog.Modules.Select(module => (module, "VCEAF")).ToArray());

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true)
            .Select(endpoint => (
                Method: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single(),
                Route: endpoint.RoutePattern.RawText!))
            .Where(endpoint => !AllowedDuringPasswordChange.Contains($"{endpoint.Method} {endpoint.Route}"))
            .ToList();

        Assert.True(endpoints.Count >= 12, $"Expected the Access endpoints, found {endpoints.Count}");

        foreach (var (method, route) in endpoints)
        {
            var path = Regex.Replace(route.Replace("{module}", "projects").Replace("{code}", "manager"), "{[^}]+}", "1");
            var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method == "GET" ? null : JsonContent.Create(new { }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await app.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {route} returned {(int)response.StatusCode}");
            using var problem = JsonDocument.Parse(body);
            Assert.Equal("password_change_required", problem.RootElement.GetProperty("code").GetString());
        }
    }
}
