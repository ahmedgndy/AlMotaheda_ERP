using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Workforce.Tests.Api;

/// <summary>
/// The weekly settlement endpoints over real HTTP without SQL Server: sign-in, permissions (a site engineer records
/// from custody but never confirms), and the form rules that run before any database work.
/// </summary>
public sealed class SettlementApiSecurityTests
{
    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>Every settlement endpoint, with a token that lacks what it needs (module:letters;module:letters).</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        // Labor money or a custody to pay from, and workers View.
        { "GET", "/api/projects/1/labor/payable", "workers:VCEA;custody:VEAF" },
        { "GET", "/api/projects/1/labor/payable", "workers:CEAF;custody:VCEAF" },
        { "GET", "/api/projects/1/labor/settlements", "workers:VCEA;custody:VEAF" },
        { "GET", "/api/projects/1/labor/settlements/2", "workers:VCEA;custody:VEAF" },
        { "POST", "/api/projects/1/labor/settlements", "workers:VEAF;custody:VCEAF" },
        { "POST", "/api/projects/1/labor/settlements", "workers:VCEA;custody:VEAF" },
        // Confirming, rejecting and reversing is custody Approve only: a site engineer (workers VC, custody VC) can't.
        { "POST", "/api/projects/1/labor/settlements/2/confirm", "workers:VC;custody:VC" },
        { "POST", "/api/projects/1/labor/settlements/2/reject", "workers:VCEAF;custody:VCEF" },
        { "POST", "/api/projects/1/labor/settlements/2/reverse", "workers:VCEAF;custody:VCEF;payments:VCEAF" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string denied)
    {
        _ = denied;
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_permission_get_403(string method, string path, string denied)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "engineer", permissions: Parse(denied));

        var response = await Send(app, method, path, token, new { rowVersion = RowVersion });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/labor/settlements", "idempotencyKey,paidOn,periodFrom,periodTo")]
    [InlineData("POST", "/api/projects/1/labor/settlements/2/confirm", "rowVersion")]
    [InlineData("POST", "/api/projects/1/labor/settlements/2/reject", "note,rowVersion")]
    [InlineData("POST", "/api/projects/1/labor/settlements/2/reverse", "note,rowVersion")]
    public async Task Empty_forms_list_their_required_fields_before_any_database_work(string method, string path, string fields)
    {
        var database = new StandInDatabase();
        await using var app = await TestApplication.StartAsync(configureBuilder: database.AddTo);
        var token = TestTokens.Create(role: "accountant", permissions: [("workers", "VCEAF"), ("custody", "VCEAF")]);

        var response = await Send(app, method, path, token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(fields.Split(','), (await ErrorFields(response)).Order(StringComparer.Ordinal));
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task A_settlement_form_checks_its_period_and_lines_before_any_database_work()
    {
        var database = new StandInDatabase();
        await using var app = await TestApplication.StartAsync(configureBuilder: database.AddTo);
        var token = TestTokens.Create(role: "engineer", permissions: [("workers", "VC"), ("custody", "VC")]);

        var response = await Send(app, "POST", "/api/projects/1/labor/settlements", token, new
        {
            periodFrom = "2026-01-09",
            periodTo = "2026-01-01",
            paidOn = "2099-01-01",
            idempotencyKey = Guid.NewGuid().ToString(),
            lines = new[] { new { workerId = 3, amount = 100m }, new { workerId = 3, amount = 50m } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["lines", "paidOn", "periodTo"], (await ErrorFields(response)).Order(StringComparer.Ordinal));
        Assert.Empty(database.Commands);
    }

    private static (string Module, string Letters)[] Parse(string permissions) =>
        permissions.Split(';').Select(entry => (entry.Split(':')[0], entry.Split(':')[1])).ToArray();
}
