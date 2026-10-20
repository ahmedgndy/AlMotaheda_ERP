using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Projects.Tests.Api.Workspace;

/// <summary>
/// The custody and supplier endpoints over real HTTP without SQL Server: sign-in, permissions, and the form rules
/// that run before any database work.
/// </summary>
public sealed class FinancialWorkspaceApiSecurityTests
{
    /// <summary>Every new endpoint, with a token that lacks what it needs (module:letters;module:letters).</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/projects/1/custody", "custody:CEAF;projects:VCEAF" },
        { "GET", "/api/projects/1/custody/2", "custody:CEAF;projects:VCEAF" },
        { "POST", "/api/projects/1/custody/fundings", "custody:VEAF;payments:VCEAF" },
        { "POST", "/api/projects/1/custody/returns", "custody:VCEF;payments:VCEAF" },
        { "GET", "/api/projects/1/supply-agreements", "projects:CEAF;procurement:VCEAF" },
        { "POST", "/api/projects/1/supply-agreements", "projects:VCAF;procurement:VCAF" },
        { "PUT", "/api/projects/1/supply-agreements/2", "projects:VCAF;procurement:VCAF" },
        { "POST", "/api/projects/1/supply-agreements/2/close", "projects:VCAF;procurement:VCAF" },
        { "GET", "/api/projects/1/suppliers", "projects:VCEA;procurement:VCEAF" },
        { "GET", "/api/suppliers/1/account", "projects:VCEA;procurement:VCEAF" },
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
        var token = TestTokens.Create(role: "manager", permissions: Parse(denied));

        var response = await Send(app, method, path, token, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/custody/fundings", "amount,idempotencyKey")]
    [InlineData("POST", "/api/projects/1/custody/returns", "amount,holderUserId,idempotencyKey,returnDate")]
    [InlineData("POST", "/api/projects/1/supply-agreements", "materialName,projectItemId,supplierId,unit")]
    [InlineData("PUT", "/api/projects/1/supply-agreements/2", "materialName,rowVersion,unit")]
    [InlineData("POST", "/api/projects/1/supply-agreements/2/close", "rowVersion")]
    public async Task Empty_forms_list_their_required_fields_before_any_database_work(string method, string path, string fields)
    {
        var database = new StandInDatabase();
        await using var app = await TestApplication.StartAsync(configureBuilder: database.AddTo);
        var token = TestTokens.Create(role: "executive", permissions: [("custody", "VCEAF"), ("projects", "VCEAF"), ("procurement", "VCEAF")]);

        var response = await Send(app, method, path, token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(fields.Split(','), (await ErrorFields(response)).Order(StringComparer.Ordinal));
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task A_cash_purchase_names_its_payee_only_when_paid_from_custody_and_never_with_a_supplier()
    {
        var database = new StandInDatabase();
        await using var app = await TestApplication.StartAsync(configureBuilder: database.AddTo);
        var token = TestTokens.Create(role: "engineer", permissions: [("projects", "VC")]);
        var supply = new Dictionary<string, object?>
        {
            ["kind"] = "Supply", ["expenseDate"] = "2026-01-10", ["itemName"] = "Sand", ["unit"] = "M3", ["quantity"] = 2m, ["unitPrice"] = 100m,
        };

        var onCredit = await Send(app, "POST", "/api/projects/1/expenses", token, new Dictionary<string, object?>(supply) { ["channel"] = "Credit", ["payeeName"] = "Merchant" });
        Assert.Contains("payeeName", await ErrorFields(onCredit));
        var both = await Send(app, "POST", "/api/projects/1/expenses", token,
            new Dictionary<string, object?>(supply) { ["channel"] = "Custody", ["payeeName"] = "Merchant", ["supplierId"] = 3 });
        Assert.Contains("payeeName", await ErrorFields(both));
        var neither = await Send(app, "POST", "/api/projects/1/expenses", token, new Dictionary<string, object?>(supply) { ["channel"] = "Custody" });
        Assert.Contains("supplierId", await ErrorFields(neither));
        Assert.Empty(database.Commands);
    }

    private static (string Module, string Letters)[] Parse(string permissions) =>
        permissions.Split(';').Select(entry => (entry.Split(':')[0], entry.Split(':')[1])).ToArray();
}
