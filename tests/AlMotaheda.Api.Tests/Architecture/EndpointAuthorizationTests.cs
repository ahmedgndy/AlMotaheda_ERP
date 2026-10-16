using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Api.Tests.Architecture;

/// <summary>
/// Every endpoint has an authorization policy (brief Phase 8). The only ones open without a token are listed here, so a
/// new anonymous endpoint fails this test until someone decides it on purpose.
/// </summary>
public sealed class EndpointAuthorizationTests
{
    /// <summary>Sign-in and token refresh: there is no token yet.</summary>
    private static readonly HashSet<string> Anonymous = new(StringComparer.Ordinal)
    {
        "POST /api/auth/login",
        "POST /api/auth/refresh",
        "POST /api/auth/logout",
    };

    [Fact]
    public async Task Every_api_endpoint_requires_authorization_unless_it_is_listed_as_anonymous()
    {
        await using var app = await TestApplication.StartAsync();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true)
            .Select(endpoint => (
                Name: $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"])} {endpoint.RoutePattern.RawText}",
                Authorized: endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(),
                Anonymous: endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null))
            .ToList();

        Assert.True(endpoints.Count > 200, $"Expected every module's endpoints, found {endpoints.Count}.");
        Assert.Empty(endpoints.Where(endpoint => !endpoint.Authorized && !endpoint.Anonymous).Select(endpoint => endpoint.Name));
        Assert.Equal(Anonymous.Order(), endpoints.Where(endpoint => endpoint.Anonymous).Select(endpoint => endpoint.Name).Distinct().Order());
    }
}
