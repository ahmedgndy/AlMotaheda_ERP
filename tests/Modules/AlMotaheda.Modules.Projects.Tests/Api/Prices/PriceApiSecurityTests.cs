using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Projects.Tests.Api.Prices;

/// <summary>
/// The company price list over real HTTP without SQL Server: sign-in, the catalog permissions, and the form rules that
/// run before any database work.
/// </summary>
public sealed class PriceApiSecurityTests
{
    private const string RowVersion = "AAAAAAAAB9E=";

    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/price-items", "V" },
        { "GET", "/api/price-items/1", "V" },
        { "POST", "/api/price-items", "C" },
        { "PUT", "/api/price-items/1", "C" },
        { "POST", "/api/price-items/1/ranges", "E" },
        { "GET", "/api/price-ranges", "V" },
        { "POST", "/api/price-ranges/1/approve", "A" },
        { "POST", "/api/price-ranges/1/reject", "A" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string letter)
    {
        _ = letter;
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_catalog_permission_get_403(string method, string path, string letter)
    {
        await using var app = await TestApplication.StartAsync();
        var others = new string("VCEAF".Where(candidate => candidate.ToString() != letter).ToArray());
        var token = TestTokens.Create(role: "executive", permissions: [("catalog", others), ("projects", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Fact]
    public async Task The_price_check_needs_projects_create_or_catalog_view()
    {
        await using var app = await TestApplication.StartAsync();
        var neither = TestTokens.Create(role: "engineer", permissions: [("projects", "V"), ("catalog", "CEAF")]);
        var recorder = TestTokens.Create(role: "engineer", permissions: [("projects", "C")]);

        var refused = await Send(app, "GET", "/api/price-items/1/check?unitPrice=1400", neither);
        var reached = await Send(app, "GET", "/api/price-items/1/check?unitPrice=-1", recorder);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reached.StatusCode);
        Assert.Equal(["unitPrice"], await ErrorFields(reached));
    }

    [Fact]
    public async Task A_range_form_reports_every_failing_field()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("catalog", "VCEAF")]);

        var response = await Send(app, "POST", "/api/price-items", token, new
        {
            nameEn = "Cement",
            classification = "Food",
            unit = "Barrel",
            minPrice = 1_200m,
            maxPrice = 1_000m,
            currency = "USD",
            effectiveFrom = "2026-02-01",
            effectiveTo = "2026-01-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["classification", "currency", "effectiveTo", "maxPrice", "nameAr", "unit"], (await ErrorFields(response)).Order());
    }

    [Fact]
    public async Task A_range_change_needs_a_reason_and_a_rejection_a_note()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("catalog", "VCEAF")]);

        var request = await Send(app, "POST", "/api/price-items/1/ranges", token, new { minPrice = 1_000m, maxPrice = 1_200m });
        var reject = await Send(app, "POST", "/api/price-ranges/1/reject", token, new { });

        Assert.Equal(["reason"], await ErrorFields(request));
        Assert.Equal(["note"], await ErrorFields(reject));
    }

    private static object? ValidBody(string method, string path) => method switch
    {
        "GET" => null,
        "PUT" => new { nameAr = "أسمنت", nameEn = "Cement", classification = "CementAggregates", status = "Active", rowVersion = RowVersion },
        _ when path.EndsWith("/ranges", StringComparison.Ordinal) => new { minPrice = 1_000m, maxPrice = 1_200m, reason = "New list" },
        _ when path.EndsWith("/approve", StringComparison.Ordinal) || path.EndsWith("/reject", StringComparison.Ordinal) => new { note = "Checked" },
        _ => new { nameAr = "أسمنت", nameEn = "Cement", classification = "CementAggregates", unit = "Ton", minPrice = 1_000m, maxPrice = 1_200m },
    };
}
