using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Office.Tests.Api;

/// <summary>Every Office endpoint over real HTTP without SQL Server: sign-in, each permission it needs, validation.</summary>
public sealed class OfficeApiSecurityTests
{
    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>Every endpoint, once per permission it needs.</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/office/home", "office:V" },
        { "GET", "/api/office/review-queue", "office:A" },
        { "GET", "/api/office/reports/summary", "office:V" },
        { "GET", "/api/office/alerts/1", "office:V" },
        { "GET", "/api/office/custodies", "office:V" },
        { "GET", "/api/office/custodies/1", "office:V" },
        { "POST", "/api/office/custodies", "office:A" },
        { "PUT", "/api/office/custodies/1", "office:A" },
        { "POST", "/api/office/custodies/1/suspend", "office:A" },
        { "POST", "/api/office/custodies/1/reopen", "office:A" },
        { "POST", "/api/office/custodies/1/close", "office:A" },
        { "GET", "/api/office/custodies/1/ledger", "office:V" },
        { "POST", "/api/office/custodies/1/returns", "office:A" },
        { "POST", "/api/office/attachments", "office:C" },
        { "GET", "/api/office/attachments/1/content", "office:V" },
        { "GET", "/api/office/price-items", "office:V" },
        { "POST", "/api/office/price-check", "office:V" },
        { "GET", "/api/office/expenses", "office:V" },
        { "GET", "/api/office/expenses/1", "office:V" },
        { "POST", "/api/office/expenses", "office:C" },
        { "PUT", "/api/office/expenses/1", "office:C" },
        { "POST", "/api/office/expenses/1/approve", "office:A" },
        { "POST", "/api/office/expenses/1/return", "office:A" },
        { "POST", "/api/office/expenses/1/reject", "office:A" },
        { "POST", "/api/office/expenses/1/reverse", "office:A" },
        { "GET", "/api/office/funding-requests", "office:V" },
        { "GET", "/api/office/funding-requests/1", "office:V" },
        { "POST", "/api/office/funding-requests", "office:C" },
        { "POST", "/api/office/funding-requests/1/approve", "office:A" },
        { "POST", "/api/office/funding-requests/1/approve", "payments:C" },
        { "POST", "/api/office/funding-requests/1/reject", "office:A" },
        { "POST", "/api/office/funding-requests/1/cancel", "office:V" },
        { "POST", "/api/office/funding-requests/1/confirm-receipt", "office:C" },
        { "GET", "/api/office/spend-requests", "office:V" },
        { "GET", "/api/office/spend-requests/1", "office:V" },
        { "POST", "/api/office/spend-requests", "office:C" },
        { "POST", "/api/office/spend-requests/1/approve", "office:A" },
        { "POST", "/api/office/spend-requests/1/reject", "office:A" },
        { "POST", "/api/office/spend-requests/1/cancel", "office:V" },
        { "POST", "/api/office/spend-requests/1/waive-after-evidence", "office:A" },
        { "GET", "/api/office/cash-counts", "office:V" },
        { "GET", "/api/office/cash-counts/1", "office:V" },
        { "POST", "/api/office/cash-counts", "office:C" },
        { "POST", "/api/office/cash-counts/1/accept", "office:A" },
        { "POST", "/api/office/cash-counts/1/adjust", "office:A" },
    };

    private static object Body => new { note = "Because", reason = "Because", rowVersion = RowVersion };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string permission)
    {
        _ = permission;
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, method == "GET" ? null : Body);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_permission_get_403(string method, string path, string permission)
    {
        await using var app = await TestApplication.StartAsync();
        var (module, letter) = (permission.Split(':')[0], permission.Split(':')[1]);
        string Letters(string candidate) => candidate == module ? "VCEAF".Replace(letter, "", StringComparison.Ordinal) : "VCEAF";
        var token = TestTokens.Create(role: "executive", permissions: [("office", Letters("office")), ("payments", Letters("payments"))]);

        var response = await Send(app, method, path, token, method == "GET" ? null : Body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [InlineData("engineer", "projects", "VC")]
    [InlineData("staff", "custody", "VC")]
    [InlineData("hr", "hr", "VCEF")]
    public async Task Roles_without_office_access_are_refused(string role, string module, string letters)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: role, permissions: [(module, letters)]);

        var response = await Send(app, "GET", "/api/office/home", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_custodian_role_can_not_review()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "office_admin", permissions: [("office", "VCE"), ("custody", "VC"), ("payments", "V")]);

        foreach (var path in new[] { "/api/office/expenses/1/approve", "/api/office/funding-requests/1/approve", "/api/office/cash-counts/1/adjust" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(app, "POST", path, token, Body)).StatusCode);
        }
    }

    [Theory]
    [InlineData("POST", "/api/office/expenses",
        "{\"category\":\"Food\",\"quantity\":0,\"unitPrice\":-1,\"discount\":-1,\"expenseDate\":\"20-10-2026\"}",
        new[] { "custodyId", "idempotencyKey", "category", "description", "quantity", "unit", "unitPrice", "discount", "expenseDate" })]
    [InlineData("POST", "/api/office/funding-requests", "{\"amount\":0}", new[] { "custodyId", "amount", "reason", "idempotencyKey" })]
    [InlineData("POST", "/api/office/spend-requests", "{\"kind\":\"Party\",\"isEmergency\":true}",
        new[] { "custodyId", "kind", "title", "description", "emergencyReason", "idempotencyKey" })]
    [InlineData("POST", "/api/office/cash-counts", "{\"countedAmount\":-3}", new[] { "custodyId", "countedAmount", "idempotencyKey" })]
    [InlineData("POST", "/api/office/expenses/1/return", "{\"rowVersion\":\"AAAAAAAAB9E=\"}", new[] { "note" })]
    [InlineData("POST", "/api/office/expenses/1/reverse", "{}", new[] { "note", "rowVersion" })]
    [InlineData("POST", "/api/office/custodies", "{\"lowBalanceThreshold\":-1}", new[] { "nameAr", "nameEn", "lowBalanceThreshold", "holderUserId" })]
    [InlineData("POST", "/api/office/custodies/1/returns", "{\"amount\":0}", new[] { "amount", "reason", "idempotencyKey" })]
    [InlineData("POST", "/api/office/price-check", "{\"unitPrice\":0}", new[] { "priceItemId", "unit", "unitPrice" })]
    public async Task Bad_input_answers_400_with_every_field(string method, string path, string json, string[] fields)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("office", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(fields.Order(), (await ErrorFields(response)).Order());
    }
}
