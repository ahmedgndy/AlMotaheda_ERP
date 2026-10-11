using System.Net;
using System.Text.Json;
using AlMotaheda.Api.Infrastructure.ApiDocumentation;

namespace AlMotaheda.Api.Tests.Infrastructure;

public sealed class ApiDocumentationTests
{
    private const string DocumentPath = "/swagger/v1/swagger.json";

    [Fact]
    public async Task OpenApi_document_is_generated_when_enabled()
    {
        // Generating the full document also catches schema-id conflicts as new feature slices are added.
        await using var app = await TestApplication.StartAsync(
            configuration: new Dictionary<string, string?> { ["ApiDocumentation:Enabled"] = "true" });

        var response = await app.Client.GetAsync(DocumentPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.StartsWith("3.", document.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("AlMotaheda ERP API", document.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task OpenApi_document_is_enabled_by_default_in_development()
    {
        await using var app = await TestApplication.StartAsync(environment: "Development");

        var response = await app.Client.GetAsync(DocumentPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_disabled_by_default_outside_development()
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.GetAsync(DocumentPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(typeof(SampleSlice.Request), "SampleSliceRequest")]
    [InlineData(typeof(SampleSlice.Response), "SampleSliceResponse")]
    [InlineData(typeof(PagedResult<SampleSlice.Response>), "PagedResultOfSampleSliceResponse")]
    [InlineData(typeof(string), "String")]
    public void Schema_ids_include_the_declaring_slice(Type type, string expectedId)
    {
        Assert.Equal(expectedId, ApiDocumentationSetup.CreateSchemaId(type));
    }
}

// Shaped like a real vertical slice: a top-level static class with nested request/response models.
internal static class SampleSlice
{
    public sealed record Request;

    public sealed record Response;
}

internal sealed record PagedResult<T>;
