using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.HR.Tests.Api;

/// <summary>Every HR endpoint over real HTTP without SQL Server: sign-in, each permission it needs, validation.</summary>
public sealed class HrApiSecurityTests
{
    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>Every endpoint, once per permission it needs.</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/hr/departments", "hr:V" },
        { "POST", "/api/hr/departments", "hr:C" },
        { "PUT", "/api/hr/departments/1", "hr:E" },
        { "GET", "/api/hr/employees", "hr:V" },
        { "GET", "/api/hr/employees/1", "hr:V" },
        { "POST", "/api/hr/employees", "hr:C" },
        { "PUT", "/api/hr/employees/1", "hr:E" },
        { "POST", "/api/hr/employees/1/end", "hr:E" },
        { "GET", "/api/hr/employees/1/documents", "hr:V" },
        { "POST", "/api/hr/employees/1/documents", "hr:C" },
        { "PUT", "/api/hr/employee-documents/1", "hr:E" },
        { "GET", "/api/hr/employees/1/salaries", "hr:V" },
        { "GET", "/api/hr/employees/1/salaries", "hr:F" },
        { "POST", "/api/hr/employees/1/salaries", "hr:E" },
        { "POST", "/api/hr/employees/1/salaries", "hr:F" },
        { "GET", "/api/hr/payroll-inputs?month=2026-10", "hr:V" },
        { "GET", "/api/hr/payroll-inputs?month=2026-10", "hr:F" },
        { "POST", "/api/hr/payroll-inputs", "hr:C" },
        { "POST", "/api/hr/payroll-inputs", "hr:F" },
        { "POST", "/api/hr/payroll-inputs/1/cancel", "hr:E" },
        { "POST", "/api/hr/payroll-inputs/1/cancel", "hr:F" },
        { "GET", "/api/hr/salary-sheets", "hr:V" },
        { "GET", "/api/hr/salary-sheets/1", "hr:V" },
        { "POST", "/api/hr/salary-sheets", "hr:C" },
        { "POST", "/api/hr/salary-sheets", "hr:F" },
        { "POST", "/api/hr/salary-sheets/1/recalculate", "hr:E" },
        { "POST", "/api/hr/salary-sheets/1/recalculate", "hr:F" },
        { "POST", "/api/hr/salary-sheets/1/submit", "hr:E" },
        { "POST", "/api/hr/salary-sheets/1/submit", "hr:F" },
        { "POST", "/api/hr/salary-sheets/1/approve", "hr:A" },
        { "POST", "/api/hr/salary-sheets/1/approve", "hr:F" },
        { "POST", "/api/hr/salary-sheets/1/return", "hr:A" },
        { "POST", "/api/hr/salary-sheets/1/return", "hr:F" },
        { "POST", "/api/hr/salary-sheets/1/payment-request", "payments:C" },
        { "POST", "/api/hr/salary-sheets/1/payment-request", "hr:V" },
        // Employees on projects, and the cases referred to HR.
        { "GET", "/api/hr/employees/1/assignments", "hr:V" },
        { "POST", "/api/hr/employees/1/assignments", "hr:E" },
        { "POST", "/api/hr/employees/1/assignments/2/end", "hr:E" },
        { "GET", "/api/hr/assignments?projectId=1", "hr:V" },
        { "GET", "/api/hr/referrals", "hr:V" },
        { "GET", "/api/hr/referrals/1", "hr:V" },
        { "POST", "/api/hr/referrals/1/start-investigation", "hr:E" },
        { "POST", "/api/hr/referrals/1/close", "hr:A" },
    };

    private static object Body => new { reason = "Because", rowVersion = RowVersion };

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
        var token = TestTokens.Create(role: "executive", permissions: [("hr", Letters("hr")), ("payments", Letters("payments"))]);

        var response = await Send(app, method, path, token, method == "GET" ? null : Body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Fact]
    public async Task The_hr_role_has_no_payments_access()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "hr", permissions: [("hr", "VCEF")]);

        var response = await Send(app, "POST", "/api/hr/salary-sheets/1/payment-request", token, Body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/hr/employees", "{\"nationalId\":\"12345\",\"kind\":\"Pilot\",\"iban\":\"not-an-iban\"}", new[] { "nameAr", "nameEn", "nationalId", "jobTitle", "departmentId", "kind", "hireDate", "iban" })]
    [InlineData("POST", "/api/hr/employees/1/salaries", "{\"basicSalary\":-1,\"fixedAllowance\":0.001}", new[] { "effectiveFrom", "basicSalary", "fixedAllowance" })]
    [InlineData("POST", "/api/hr/payroll-inputs", "{\"month\":\"2026-13\",\"kind\":\"Gift\",\"amount\":0}", new[] { "employeeId", "month", "kind", "amount", "reason" })]
    [InlineData("POST", "/api/hr/salary-sheets", "{\"month\":\"October\"}", new[] { "month" })]
    [InlineData("POST", "/api/hr/salary-sheets/1/return", "{\"rowVersion\":\"AAAAAAAAB9E=\"}", new[] { "reason" })]
    [InlineData("POST", "/api/hr/salary-sheets/1/payment-request", "{\"method\":\"Gold\"}", new[] { "requestedDate", "method", "rowVersion" })]
    [InlineData("POST", "/api/hr/employees/1/end", "{}", new[] { "endDate", "reason", "rowVersion" })]
    [InlineData("POST", "/api/hr/employees/1/assignments", "{\"role\":\"Pilot\",\"startDate\":\"2999-01-01\"}", new[] { "projectId", "role", "startDate" })]
    [InlineData("POST", "/api/hr/referrals/1/close", "{\"outcome\":\"DeductionApplied\"}", new[] { "deduction", "note", "rowVersion" })]
    [InlineData("POST", "/api/hr/referrals/1/close", "{\"outcome\":\"NoAction\",\"note\":\"x\",\"rowVersion\":\"AAAAAAAAB9E=\",\"deduction\":{\"amount\":1}}", new[] { "deduction" })]
    public async Task Bad_input_answers_400_with_every_field(string method, string path, string json, string[] fields)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("hr", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(fields.Order(), (await ErrorFields(response)).Order());
    }

    [Fact]
    public async Task A_month_that_has_not_started_can_not_be_prepared()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("hr", "VCEAF")]);

        var response = await Send(app, "POST", "/api/hr/salary-sheets", token, new { month = "2999-01" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("month", await ErrorFields(response));
    }
}
