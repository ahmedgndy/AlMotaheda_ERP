using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AlMotaheda.Api.Tests;

/// <summary>Small helpers for calling the test host over HTTP with a bearer token.</summary>
internal static class ApiCalls
{
    public static Task<HttpResponseMessage> Send(TestApplication app, string method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" || body is null ? null : JsonContent.Create(body),
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return app.Client.SendAsync(request);
    }

    public static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>The machine-readable <c>code</c> of an error response, or <c>null</c> when there is none.</summary>
    public static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await Json(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    /// <summary>The names of the fields listed under <c>errors</c> in a 400 response.</summary>
    public static async Task<IReadOnlyList<string>> ErrorFields(HttpResponseMessage response) =>
        (await Json(response)).TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(field => field.Name).ToList()
            : [];
}
