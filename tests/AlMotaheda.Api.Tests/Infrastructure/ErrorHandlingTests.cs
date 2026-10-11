using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AlMotaheda.Api.Tests.Infrastructure;

public sealed class ErrorHandlingTests
{
    private const string ProblemJson = "application/problem+json";
    private const string SecretMessage = "Connection string Server=prod-db;Password=secret";

    [Fact]
    public async Task Unhandled_exception_returns_500_problem_details_without_internal_details()
    {
        await using var app = await TestApplication.StartAsync(configureApp: MapThrowingEndpoint);

        var response = await app.Client.GetAsync("/test/throw");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("secret", content, StringComparison.OrdinalIgnoreCase);

        using var problem = JsonDocument.Parse(content);
        var root = problem.RootElement;
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("/test/throw", root.GetProperty("instance").GetString());
        // The bare 32-character trace id, exactly as it appears in the logs' TraceId field.
        Assert.Matches("^[0-9a-f]{32}$", root.GetProperty("traceId").GetString());
        Assert.False(root.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task Exception_message_is_returned_only_in_development()
    {
        await using var app = await TestApplication.StartAsync(environment: "Development", configureApp: MapThrowingEndpoint);

        var response = await app.Client.GetAsync("/test/throw");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(SecretMessage, problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Unknown_route_returns_404_problem_details()
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData("Testing")]     // Minimal APIs return 400 directly.
    [InlineData("Development")] // Minimal APIs throw BadHttpRequestException, handled by GlobalExceptionHandler.
    public async Task Malformed_json_returns_400_problem_details(string environment)
    {
        await using var app = await TestApplication.StartAsync(
            environment: environment,
            configureApp: endpoints => endpoints.MapPost("/test/echo", (EchoRequest request) => Results.Ok(request)));

        var response = await app.Client.PostAsync(
            "/test/echo",
            new StringContent("{ not valid json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
    }

    private static void MapThrowingEndpoint(WebApplication app) =>
        app.MapGet("/test/throw", IResult () => throw new InvalidOperationException(SecretMessage));

    public sealed record EchoRequest(string Name);
}
