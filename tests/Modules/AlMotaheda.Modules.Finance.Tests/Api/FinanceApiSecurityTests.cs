using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Finance.Tests.Api;

/// <summary>Every Finance endpoint over real HTTP without SQL Server: sign-in, the permission each needs, validation.</summary>
public sealed class FinanceApiSecurityTests
{
    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>Every endpoint, with the permission it needs.</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/payment-requests", "payments:V" },
        { "GET", "/api/payment-requests/1", "payments:V" },
        { "POST", "/api/payment-requests/1/approve", "payments:A" },
        { "POST", "/api/payment-requests/1/reject", "payments:A" },
        { "POST", "/api/payment-requests/1/approve-and-pay", "payments:A" },
        { "POST", "/api/payment-requests/1/send-to-bank", "finance:E" },
        { "POST", "/api/payment-requests/1/confirm-payment", "finance:E" },
        { "POST", "/api/payment-requests/1/bank-failure", "finance:E" },
        { "POST", "/api/payment-requests/1/cancel", "payments:V" },
        { "GET", "/api/ledger", "finance:F" },
    };

    private static object Body(string path) => new { note = "OK", reason = "Because", rowVersion = RowVersion };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string permission)
    {
        _ = permission;
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, method == "POST" ? Body(path) : null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_permission_get_403(string method, string path, string permission)
    {
        await using var app = await TestApplication.StartAsync();
        var (module, letter) = (permission.Split(':')[0], permission.Split(':')[1]);
        var others = new string("VCEAF".Where(candidate => !letter.Contains(candidate)).ToArray());
        var token = TestTokens.Create(role: "accountant", permissions: [(module, others), (module == "payments" ? "finance" : "payments", "VCEAF")]);

        var response = await Send(app, method, path, token, method == "POST" ? Body(path) : null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [InlineData("/api/payment-requests/1/reject", "note")]
    [InlineData("/api/payment-requests/1/cancel", "reason")]
    [InlineData("/api/payment-requests/1/bank-failure", "reason")]
    [InlineData("/api/payment-requests/1/approve", "rowVersion")]
    public async Task A_missing_note_reason_or_row_version_answers_400(string path, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("payments", "VCEAF"), ("finance", "VCEAF")]);

        var response = await Send(app, "POST", path, token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFields(response));
    }

    [Theory]
    [InlineData("/api/payment-requests/1/confirm-payment", "{\"paidOn\":\"2999-01-01\",\"rowVersion\":\"AAAAAAAAB9E=\"}", "paidOn")]
    [InlineData("/api/payment-requests?status=Lost", null, "status")]
    [InlineData("/api/ledger?from=2026-10-10&to=2026-10-01", null, "to")]
    [InlineData("/api/ledger?account=Safe", null, "account")]
    public async Task Bad_input_answers_400_with_the_field(string path, string? json, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("payments", "VCEAF"), ("finance", "VCEAF")]);

        var response = json is null
            ? await Send(app, "GET", path, token)
            : await Send(app, "POST", path, token, System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFields(response));
    }
}
