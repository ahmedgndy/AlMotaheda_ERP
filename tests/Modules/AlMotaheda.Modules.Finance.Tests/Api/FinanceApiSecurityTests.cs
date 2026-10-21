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
        // The treasury and the custody hierarchy.
        { "GET", "/api/treasury", "finance:F" },
        { "PUT", "/api/treasury/Cash/opening-balance", "finance:A" },
        { "GET", "/api/custody-accounts", "custody:V" },
        { "GET", "/api/custody-accounts/1", "custody:V" },
        { "POST", "/api/custody-accounts", "custody:A" },
        { "POST", "/api/custody-accounts/1/close", "custody:A" },
        { "GET", "/api/custody-accounts/1/cash-counts", "custody:V" },
        { "POST", "/api/custody-accounts/1/cash-counts", "custody:CA" },
        { "GET", "/api/custody-accounts/1/adjustments", "custody:V" },
        { "POST", "/api/custody-accounts/1/adjustments", "custody:A" },
        { "GET", "/api/custody-accounts/1/period-closes", "custody:V" },
        { "POST", "/api/custody-accounts/1/period-closes", "custody:A" },
        { "GET", "/api/custody-transfers", "custody:V" },
        { "GET", "/api/custody-transfers/1", "custody:V" },
        { "POST", "/api/custody-transfers", "custody:CA" },
        { "POST", "/api/custody-transfers/1/approve", "payments:A" },
        { "POST", "/api/custody-transfers/1/reject", "payments:A" },
        { "POST", "/api/custody-transfers/1/disburse", "custody:V" },
        { "POST", "/api/custody-transfers/1/receive", "custody:V" },
        { "POST", "/api/custody-transfers/1/cancel", "custody:V" },
        { "POST", "/api/custody-transfers/1/reverse", "custody:A" },
        { "GET", "/api/payroll-batches", "custody:VF" },
        { "GET", "/api/payroll-batches/1", "custody:VF" },
        { "POST", "/api/payroll-batches", "custody:A" },
        { "POST", "/api/payroll-batches/1/close", "custody:A" },
    };

    private static object Body(string path) => new { note = "OK", reason = "Because", rowVersion = RowVersion };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string permission)
    {
        _ = permission;
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, method == "GET" ? null : Body(path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_permission_get_403(string method, string path, string permission)
    {
        await using var app = await TestApplication.StartAsync();
        var (module, letter) = (permission.Split(':')[0], permission.Split(':')[1]);
        var others = new string("VCEAF".Where(candidate => !letter.Contains(candidate)).ToArray());
        var modules = new[] { "payments", "finance", "custody" }.Where(candidate => candidate != module).Select(candidate => (candidate, "VCEAF")).ToList();
        var token = TestTokens.Create(role: "accountant", permissions: [(module, others), .. modules]);

        var response = await Send(app, method, path, token, method == "GET" ? null : Body(path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Fact]
    public async Task The_opening_balance_needs_an_executive_not_only_the_permission()
    {
        await using var app = await TestApplication.StartAsync();
        var accountant = TestTokens.Create(role: "accountant", permissions: [("finance", "VCEAF")]);

        var response = await Send(app, "PUT", "/api/treasury/Cash/opening-balance", accountant, new { openingBalance = 1_000_000m, openingDate = "2026-01-01", rowVersion = RowVersion });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/custody-transfers", "{}", new[] { "amount", "destination", "idempotencyKey", "source" })]
    [InlineData("/api/custody-transfers", "{\"amount\":0,\"purpose\":\"Gift\",\"source\":{\"kind\":\"Treasury\",\"treasuryAccount\":\"Safe\"},\"destination\":{\"kind\":\"Custody\"},\"idempotencyKey\":\"x\"}", new[] { "amount", "destination.accountKind", "idempotencyKey", "purpose", "source.treasuryAccount" })]
    [InlineData("/api/custody-accounts", "{\"kind\":\"Wallet\",\"holderUserId\":0}", new[] { "holderUserId", "kind" })]
    [InlineData("/api/custody-accounts/1/adjustments", "{\"amount\":0}", new[] { "amount", "evidenceReference", "idempotencyKey", "reason" })]
    [InlineData("/api/custody-accounts/1/cash-counts", "{\"countedAmount\":-1}", new[] { "countedAmount", "idempotencyKey" })]
    [InlineData("/api/custody-accounts/1/period-closes", "{\"periodEnd\":\"2999-01-01\"}", new[] { "periodEnd" })]
    [InlineData("/api/payroll-batches", "{\"kind\":\"DailyLabor\",\"periodFrom\":\"2026-02-01\",\"periodTo\":\"2026-01-01\"}", new[] { "allocations", "periodTo" })]
    [InlineData("/api/custody-transfers/1/reverse", "{}", new[] { "reason", "rowVersion" })]
    public async Task A_bad_custody_form_lists_every_failing_field(string path, string json, string[] fields)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("payments", "VCEAF"), ("finance", "VCEAF"), ("custody", "VCEAF")]);

        var response = await Send(app, "POST", path, token, System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(fields.Order(), (await ErrorFields(response)).Order());
    }

    [Fact]
    public async Task A_transfer_out_of_the_treasury_needs_finance_create_besides_custody()
    {
        await using var app = await TestApplication.StartAsync();
        var engineer = TestTokens.Create(role: "engineer", permissions: [("custody", "VC")]);

        var response = await Send(app, "POST", "/api/custody-transfers", engineer, new
        {
            amount = 100m,
            source = new { kind = "Treasury", treasuryAccount = "Cash" },
            destination = new { kind = "Custody", accountId = 1 },
            idempotencyKey = Guid.NewGuid().ToString(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
