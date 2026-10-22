using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Reports.Tests.Api;

/// <summary>The report endpoints over real HTTP without SQL Server: sign-in, who sees which report, refusals, filters.</summary>
public sealed class ReportApiTests
{
    private static readonly string[] AllCodes =
        ["EQP-COST", "EQP-FUEL", "FIN-ACTIVITY", "FIN-CASH", "FIN-CUSTODY", "FIN-CUSTODY-RECON", "FIN-LEDGER", "FIN-PARTY-PAYMENTS", "FIN-PAYROLL-BATCH", "FIN-RECON", "FIN-REQUESTS", "FIN-TREASURY",
         "FLT-CLAIMS", "FLT-VEHICLE-CUSTODY", "FLT-VEHICLE-EXPENSE-LINES", "FLT-VEHICLE-EXPENSES", "HR-SALARIES", "PRICE-CHANGES", "PRICE-EXCEPTIONS", "PRICE-VARIANCE", "PRJ-COST", "PRJ-ITEMS", "SUB-ACCOUNT", "SUP-ACCOUNT", "WRK-PAYROLL"];

    [Fact]
    public async Task Every_module_offers_well_formed_reports_with_unique_codes()
    {
        await using var app = await TestApplication.StartAsync();
        using var scope = app.Services.CreateScope();

        var reports = scope.ServiceProvider.GetServices<IReportSource>().SelectMany(source => source.Reports).ToList();

        Assert.Equal(AllCodes, reports.Select(report => report.Code).Order());
        Assert.All(reports, report =>
        {
            Assert.NotEmpty(report.AnyOf);
            Assert.False(string.IsNullOrWhiteSpace(report.NameAr));
            Assert.Equal(report.Columns.Count, report.Columns.Select(column => column.Key).Distinct().Count());
            Assert.All(report.Columns, column => Assert.Contains(column.Type, new[] { "Text", "Date", "Money", "Quantity", "Integer" }));
            Assert.All(report.Filters, filter => Assert.Contains(filter.Type, new[] { "Date", "Month", "Id", "Option" }));
            Assert.All(report.Columns.Where(column => column.Total), column => Assert.Contains(column.Type, new[] { "Money", "Quantity", "Integer" }));
        });
    }

    [Theory]
    [InlineData("/api/reports")]
    [InlineData("/api/reports/FIN-LEDGER")]
    [InlineData("/api/reports/FIN-LEDGER/excel")]
    [InlineData("/api/reports/FIN-LEDGER/print")]
    [InlineData("/api/report-exports")]
    [InlineData("/api/report-exports/1/file")]
    public async Task Requests_without_a_token_get_401(string path)
    {
        await using var app = await TestApplication.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(app, "GET", path, token: null)).StatusCode);
    }

    [Theory]
    [InlineData("accountant", "finance:VCEAF,payments:VCF,projects:VAF,catalog:VF,workers:VCEAF,subcontractors:VCEF,equipment:VAF,hr:VAF",
        new[] { "EQP-COST", "EQP-FUEL", "FIN-ACTIVITY", "FIN-CASH", "FIN-CUSTODY", "FIN-CUSTODY-RECON", "FIN-LEDGER", "FIN-PARTY-PAYMENTS", "FIN-PAYROLL-BATCH", "FIN-RECON", "FIN-REQUESTS", "FIN-TREASURY",
                "HR-SALARIES", "PRICE-CHANGES", "PRICE-EXCEPTIONS", "PRICE-VARIANCE", "PRJ-COST", "PRJ-ITEMS", "SUB-ACCOUNT", "SUP-ACCOUNT", "WRK-PAYROLL" })]
    [InlineData("hr", "hr:VCEF", new[] { "HR-SALARIES" })]
    [InlineData("manager", "projects:VCEF,workers:VCAF,subcontractors:VCEF,payments:V", new[] { "PRJ-COST", "PRJ-ITEMS", "SUB-ACCOUNT", "SUP-ACCOUNT", "WRK-PAYROLL" })]
    [InlineData("engineer", "projects:VC,workers:VC,site:VC", new string[0])]
    [InlineData("purchasing", "procurement:VCEF", new string[0])]
    public async Task The_catalog_lists_only_the_reports_the_caller_may_run(string role, string permissions, string[] expected)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: role, permissions: [.. permissions.Split(',').Select(pair => (pair.Split(':')[0], pair.Split(':')[1]))]);

        var body = await Json(await Send(app, "GET", "/api/reports", token));

        Assert.Equal(expected, body.GetProperty("reports").EnumerateArray().Select(report => report.GetProperty("code").GetString()).Order());
    }

    [Theory]
    [InlineData("/api/reports/HR-SALARIES?fromMonth=2026-01&toMonth=2026-02")]
    [InlineData("/api/reports/FIN-LEDGER/excel?from=2026-01-01&to=2026-01-31")]
    [InlineData("/api/reports/WRK-PAYROLL/print?from=2026-01-01&to=2026-01-31")]
    public async Task A_report_the_caller_may_not_run_answers_403(string path)
    {
        await using var app = await TestApplication.StartAsync();
        var engineer = TestTokens.Create(role: "engineer", permissions: [("projects", "VC"), ("workers", "VC")]);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(app, "GET", path, engineer)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_report_answers_404_and_bad_filters_answer_400_with_the_field()
    {
        await using var app = await TestApplication.StartAsync();
        var executive = TestTokens.Create(role: "executive", permissions: [("finance", "VCEAF")]);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(app, "GET", "/api/reports/NOPE", executive)).StatusCode);

        var bad = await Send(app, "GET", "/api/reports/FIN-CASH?from=2026-10-10&to=2026-10-01&period=Year", executive);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(["period", "to"], (await ErrorFields(bad)).Order());

        var missing = await Send(app, "GET", "/api/reports/FIN-LEDGER?pageSize=1000", executive);
        Assert.Equal(["pageSize"], await ErrorFields(missing));
    }
}
