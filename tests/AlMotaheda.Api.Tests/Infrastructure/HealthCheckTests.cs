using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AlMotaheda.Api.Tests.Infrastructure;

public sealed class HealthCheckTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    public async Task Health_endpoint_returns_healthy_json(string path)
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Failing_dependency_makes_readiness_unhealthy_but_not_liveness()
    {
        await using var app = await TestApplication.StartAsync(configureBuilder: builder =>
            builder.Services.AddHealthChecks()
                .AddCheck("failing-dependency", () => HealthCheckResult.Unhealthy("Simulated outage")));

        var readiness = await app.Client.GetAsync("/health");
        var liveness = await app.Client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        using var body = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
        var check = Assert.Single(body.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal("failing-dependency", check.GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    [Fact]
    public async Task Responses_do_not_advertise_the_web_server()
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.GetAsync("/health/live");

        Assert.False(response.Headers.Contains("Server"));
    }
}
