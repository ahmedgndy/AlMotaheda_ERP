using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Procurement.Tests.Api;

/// <summary>Every Procurement endpoint over real HTTP without SQL Server: sign-in, the permission each needs, validation.</summary>
public sealed class ProcurementApiSecurityTests
{
    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>Every endpoint guarded by a permission, with the permission it needs.</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/procurement/policy", "procurement:V" },
        { "POST", "/api/procurement/requisitions", "procurement:C" },
        { "GET", "/api/procurement/requisitions", "procurement:V" },
        { "GET", "/api/procurement/requisitions/1", "procurement:V" },
        { "GET", "/api/procurement/requisitions/1/chain", "procurement:V" },
        { "POST", "/api/procurement/requisitions/1/approve", "procurement:A" },
        { "POST", "/api/procurement/requisitions/1/reject", "procurement:A" },
        { "POST", "/api/procurement/requisitions/1/cancel", "procurement:V" },
        { "GET", "/api/procurement/quotation-requests/1", "procurement:V" },
        { "POST", "/api/procurement/quotation-requests/1/quotations", "procurement:E" },
        { "DELETE", "/api/procurement/quotations/1", "procurement:E" },
        { "POST", "/api/procurement/quotation-requests/1/recommend", "procurement:E" },
        { "POST", "/api/procurement/quotation-requests/1/submit", "procurement:E" },
        { "GET", "/api/procurement/purchase-orders", "procurement:V" },
        { "GET", "/api/procurement/purchase-orders/1", "procurement:V" },
        { "POST", "/api/procurement/purchase-orders", "procurement:E" },
        { "POST", "/api/procurement/purchase-orders/1/revise", "procurement:E" },
        { "POST", "/api/procurement/purchase-orders/1/cancel", "procurement:E" },
        { "POST", "/api/procurement/purchase-orders/1/receipts", "site:C" },
        { "GET", "/api/procurement/goods-receipts/1", "procurement:V" },
        { "GET", "/api/procurement/supplier-invoices", "procurement:V" },
        { "GET", "/api/procurement/supplier-invoices/1", "procurement:V" },
        { "POST", "/api/procurement/purchase-orders/1/invoices", "finance:C" },
        { "POST", "/api/procurement/supplier-invoices/1/resolve", "finance:A" },
    };

    /// <summary>The Executive-only steps (role check first, then the database).</summary>
    public static TheoryData<string> ExecutiveOnly => new()
    {
        "/api/procurement/requisitions/1/quotation-request",
        "/api/procurement/quotation-requests/1/select",
        "/api/procurement/quotation-requests/1/return",
    };

    private static object Body => new { reason = "Because", note = "Note", rowVersion = RowVersion };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string permission)
    {
        _ = permission;
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, method is "POST" ? Body : null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_permission_get_403(string method, string path, string permission)
    {
        await using var app = await TestApplication.StartAsync();
        var (module, letter) = (permission.Split(':')[0], permission.Split(':')[1]);
        string Letters(string candidate) => candidate == module ? "VCEAF".Replace(letter, "", StringComparison.Ordinal) : "VCEAF";
        var token = TestTokens.Create(role: "accountant",
            permissions: [("procurement", Letters("procurement")), ("site", Letters("site")), ("finance", Letters("finance"))]);

        var response = await Send(app, method, path, token, method is "POST" ? Body : null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [MemberData(nameof(ExecutiveOnly))]
    public async Task Quotation_decisions_are_for_the_executive_only(string path)
    {
        await using var app = await TestApplication.StartAsync();
        var purchasing = TestTokens.Create(role: "purchasing", permissions: [("procurement", "VCEAF")]);

        var response = await Send(app, "POST", path, purchasing, new { quotationId = 1, reason = "Cheapest", note = "Again", rowVersion = RowVersion });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_policy_is_changed_by_the_executive_only()
    {
        await using var app = await TestApplication.StartAsync();
        var manager = TestTokens.Create(role: "manager", permissions: [("procurement", "VCEAF"), ("settings", "VCEAF")]);

        var response = await Send(app, "PUT", "/api/procurement/policy", manager, new { autoApproveRequisitions = false, priceTolerancePercent = 0, rowVersion = RowVersion });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/procurement/requisitions", "{\"lines\":[{\"quantity\":0},{\"unit\":\"Barrel\",\"quantity\":1}]}", new[] { "projectId", "lines[0].quantity", "lines[0].description", "lines[0].unit", "lines[1].description", "lines[1].unit" })]
    [InlineData("POST", "/api/procurement/requisitions", "{\"projectId\":1,\"lines\":[]}", new[] { "lines" })]
    [InlineData("POST", "/api/procurement/purchase-orders", "{\"lines\":[{\"quantity\":-1,\"unitPrice\":0}]}", new[] { "requisitionId", "lines[0].requisitionLineId", "lines[0].quantity", "lines[0].unitPrice" })]
    [InlineData("POST", "/api/procurement/purchase-orders/1/receipts", "{\"receivedOn\":\"2999-01-01\",\"lines\":[]}", new[] { "receivedOn", "lines" })]
    [InlineData("POST", "/api/procurement/purchase-orders/1/invoices", "{\"lines\":[{\"orderLineId\":1,\"quantity\":1}]}", new[] { "invoiceNo", "invoiceDate", "lines[0].unitPrice" })]
    [InlineData("POST", "/api/procurement/supplier-invoices/1/resolve", "{\"resolution\":\"CreditNote\",\"reason\":\"x\"}", new[] { "creditNoteNo", "rowVersion" })]
    [InlineData("POST", "/api/procurement/purchase-orders/1/revise", "{\"lines\":[{\"lineId\":1,\"quantity\":1,\"unitPrice\":1}]}", new[] { "reason", "rowVersion" })]
    [InlineData("PUT", "/api/procurement/policy", "{\"priceTolerancePercent\":120}", new[] { "autoApproveRequisitions", "priceTolerancePercent", "rowVersion" })]
    [InlineData("POST", "/api/procurement/quotation-requests/1/quotations", "{\"deliveryDays\":-1,\"lines\":[{\"requisitionLineId\":1}]}", new[] { "supplierId", "deliveryDays", "lines[0].unitPrice" })]
    public async Task Bad_input_answers_400_with_every_field(string method, string path, string json, string[] fields)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("procurement", "VCEAF"), ("site", "VCEAF"), ("finance", "VCEAF")]);

        var response = await Send(app, method, path, token, System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(fields.Order(), (await ErrorFields(response)).Order());
    }

    [Theory]
    [InlineData("GET", "/api/suppliers")]
    [InlineData("POST", "/api/suppliers")]
    [InlineData("PUT", "/api/suppliers/1")]
    public async Task Suppliers_are_closed_to_a_role_with_neither_projects_nor_procurement(string method, string path)
    {
        await using var app = await TestApplication.StartAsync();
        var hr = TestTokens.Create(role: "hr", permissions: [("hr", "VCEAF")]);

        var response = await Send(app, method, path, hr, method == "GET" ? null : new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
