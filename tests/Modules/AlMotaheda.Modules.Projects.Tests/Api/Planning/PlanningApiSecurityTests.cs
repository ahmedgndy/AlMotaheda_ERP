using System.Globalization;
using System.Net;
using System.Security.Claims;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Projects.Tests.Api.Planning;

/// <summary>
/// The budget, catalog, daily report, report request, overview and report endpoints over real HTTP without SQL
/// Server: sign-in, permissions, project scope (404), validation (400), and the rule checks that run before any
/// database read.
/// </summary>
public sealed class PlanningApiSecurityTests
{
    private const string Executive = "executive";
    private const string Manager = "manager";
    private const string RowVersion = "AAAAAAAAB9E=";

    private static readonly string[] Modules = ["projects", "catalog", "site", "reports", "payments"];

    /// <summary>Every endpoint of this part and the permission it needs ("module:letters").</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/projects/1/budgets", "projects:F" },
        { "GET", "/api/projects/1/budgets/2", "projects:F" },
        { "POST", "/api/projects/1/budgets", "projects:E" },
        { "PUT", "/api/projects/1/budgets/2", "projects:E" },
        { "POST", "/api/projects/1/budgets/2/approve", "projects:A" },
        { "GET", "/api/projects/1/budget-comparison", "projects:F" },
        { "GET", "/api/projects/1/catalog-items", "catalog:V" },
        { "GET", "/api/projects/1/catalog-items/2", "catalog:V" },
        { "POST", "/api/projects/1/catalog-items", "catalog:C" },
        { "PUT", "/api/projects/1/catalog-items/2", "catalog:C" },
        { "POST", "/api/projects/1/catalog-items/2/price-changes", "catalog:E" },
        { "GET", "/api/projects/1/price-changes", "catalog:V" },
        { "POST", "/api/projects/1/price-changes/2/approve", "catalog:A" },
        { "POST", "/api/projects/1/price-changes/2/reject", "catalog:A" },
        { "GET", "/api/projects/1/daily-reports", "site:V" },
        { "GET", "/api/projects/1/daily-reports/2", "site:V" },
        { "POST", "/api/projects/1/daily-reports", "site:C" },
        { "PUT", "/api/projects/1/daily-reports/2", "site:C" },
        { "POST", "/api/projects/1/daily-reports/2/review", "site:A" },
        { "POST", "/api/projects/1/daily-reports/2/return", "site:A" },
        { "GET", "/api/projects/1/report-requests", "reports:V" },
        { "GET", "/api/projects/1/report-requests/2", "reports:V" },
        { "POST", "/api/projects/1/report-requests", "reports:E" },
        { "POST", "/api/projects/1/report-requests/2/start", "reports:V" },
        { "POST", "/api/projects/1/report-requests/2/submit", "reports:V" },
        { "POST", "/api/projects/1/report-requests/2/complete", "reports:A" },
        { "POST", "/api/projects/1/report-requests/2/return", "reports:A" },
        { "POST", "/api/projects/1/report-requests/2/cancel", "reports:V" },
        { "GET", "/api/projects/1/overview", "projects:F" },
        { "GET", "/api/projects/1/reports/cost-summary", "projects:F" },
        { "GET", "/api/projects/1/reports/obligations", "projects:F" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path, string permission)
    {
        _ = permission;
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await CodeOf(response));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_the_permission_get_403(string method, string path, string permission)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Manager, permissions: AllBut(permission));

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_project_the_caller_cannot_see_answers_404_on_every_endpoint(string method, string path, string permission)
    {
        _ = permission;
        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: Executive, permissions: Modules.Select(module => (module, "VCEAF")).ToArray());

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await CodeOf(response));
    }

    /// <summary>For every endpoint, a request that breaks every rule it can, and the fields it must report at once.</summary>
    public static TheoryData<string, string, string, string[]> InvalidRequests => new()
    {
        { "GET", "/api/projects/1/budgets?page=0&pageSize=500", "", ["page", "pageSize"] },
        { "GET", "/api/projects/1/budget-comparison?budgetId=0", "", ["budgetId"] },
        {
            "POST", "/api/projects/1/budgets",
            """{"title":"%T201%","notes":"%T1001%","copyFromBudgetId":0,"lines":[{"costGroup":"Food","projectItemId":0,"description":"","unit":"Box","quantity":-1,"unitPrice":0.001},{"costGroup":"Labor","description":"Crew","amount":0},null]}""",
            ["copyFromBudgetId", "lines[0].costGroup", "lines[0].description", "lines[0].projectItemId", "lines[0].quantity", "lines[0].unit", "lines[0].unitPrice", "lines[1].amount", "lines[2]", "notes", "title"]
        },
        {
            "PUT", "/api/projects/1/budgets/2",
            """{"lines":[{"costGroup":"Materials","description":"Cement","quantity":10}]}""",
            ["lines[0].unitPrice", "rowVersion"]
        },
        { "POST", "/api/projects/1/budgets/2/approve", "{}", ["rowVersion"] },
        { "GET", "/api/projects/1/catalog-items?category=Food&status=Gone&priceStatus=Cheap&page=0", "", ["category", "page", "priceStatus", "status"] },
        {
            "POST", "/api/projects/1/catalog-items",
            """{"nameAr":"","nameEn":"","category":"Food","unit":"Box","price":0,"effectiveFrom":"01/02/2026","estimatedQuantity":-1,"supplierId":0}""",
            ["category", "effectiveFrom", "estimatedQuantity", "nameAr", "nameEn", "price", "supplierId", "unit"]
        },
        {
            "PUT", "/api/projects/1/catalog-items/2",
            """{"nameAr":"أسمنت","nameEn":"Cement","category":"CementAggregates","unit":"Bag","status":"Gone"}""",
            ["rowVersion", "status"]
        },
        { "POST", "/api/projects/1/catalog-items/2/price-changes", """{"price":-5,"effectiveFrom":"tomorrow","reason":" "}""", ["effectiveFrom", "price", "reason"] },
        { "GET", "/api/projects/1/price-changes?status=Maybe&pageSize=0", "", ["pageSize", "status"] },
        { "POST", "/api/projects/1/price-changes/2/approve", """{"note":"%T501%"}""", ["note"] },
        { "POST", "/api/projects/1/price-changes/2/reject", "{}", ["note"] },
        { "GET", "/api/projects/1/daily-reports?status=Lost&from=2026-02-10&to=2026-02-01&authorUserId=0", "", ["authorUserId", "status", "to"] },
        {
            "POST", "/api/projects/1/daily-reports",
            """{"reportDate":"2999-01-01","workersPresent":-1,"lines":[{"workType":"Dancing","description":"","unit":"Box","quantity":0,"performedBy":"Subcontractor"}],"materials":[{"materialName":"","unit":"Bag","quantity":0}],"equipment":[{"equipmentId":0,"equipmentName":"Loader","hours":25}]}""",
            ["equipment[0].equipmentId", "equipment[0].hours", "lines[0].description", "lines[0].quantity", "lines[0].subcontractorName", "lines[0].unit", "lines[0].workType", "materials[0].materialName", "materials[0].quantity", "reportDate", "workersPresent"]
        },
        { "PUT", "/api/projects/1/daily-reports/2", """{"lines":[]}""", ["lines", "rowVersion"] },
        { "POST", "/api/projects/1/daily-reports/2/review", """{"note":"%T501%"}""", ["note", "rowVersion"] },
        { "POST", "/api/projects/1/daily-reports/2/return", """{"rowVersion":"x"}""", ["note", "rowVersion"] },
        { "GET", "/api/projects/1/report-requests?status=Lost&page=0", "", ["page", "status"] },
        {
            "POST", "/api/projects/1/report-requests",
            """{"recipientUserId":0,"type":"Gossip","priority":"Whenever","title":"","dueDate":"2000-01-01","responseText":"Already done"}""",
            ["dueDate", "priority", "recipientUserId", "responseText", "title", "type"]
        },
        { "POST", "/api/projects/1/report-requests/2/start", "{}", ["rowVersion"] },
        { "POST", "/api/projects/1/report-requests/2/submit", """{"rowVersion":"AAAAAAAAB9E="}""", ["responseText"] },
        { "POST", "/api/projects/1/report-requests/2/complete", "{}", ["rowVersion"] },
        { "POST", "/api/projects/1/report-requests/2/return", "{}", ["note", "rowVersion"] },
        { "POST", "/api/projects/1/report-requests/2/cancel", """{"note":"%T501%","rowVersion":"AAAAAAAAB9E="}""", ["note"] },
        { "GET", "/api/projects/1/reports/cost-summary?from=2026-03-01&to=2026-02-01", "", ["to"] },
        { "GET", "/api/projects/1/reports/cost-summary?from=March&to=2026-13-01", "", ["from", "to"] },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_input_reports_every_failing_field_before_any_database_work(string method, string path, string body, string[] fields)
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(userId: 7, role: Executive, permissions: Modules.Select(module => (module, "VCEAF")).ToArray());

        var response = await Send(app, method, path, token, body.Length == 0 ? null : System.Text.Json.JsonDocument.Parse(Expand(body)).RootElement);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
        Assert.Equal(fields.Order(StringComparer.Ordinal), (await ErrorFields(response)).Order(StringComparer.Ordinal));
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task Approving_a_budget_needs_the_executive_role_as_well_as_the_permission()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, ProjectResponsibilities.ProjectManager, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("projects", "VCEAF")]);

        var response = await Send(app, "POST", "/api/projects/1/budgets/2/approve", token, new { rowVersion = RowVersion });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task Only_an_executive_or_the_projects_manager_prepares_a_budget()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, ProjectResponsibilities.SiteEngineer, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("projects", "VCEF")]);

        var response = await Send(app, "POST", "/api/projects/1/budgets", token, ValidBody("POST", "/api/projects/1/budgets"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task A_completed_project_refuses_a_new_budget_from_its_manager()
    {
        var (app, _) = await StartAsync(new VisibleProject(IsCompleted: true, ProjectResponsibilities.ProjectManager, Executive: false));
        await using var __ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("projects", "VCEF")]);

        var response = await Send(app, "POST", "/api/projects/1/budgets", token, ValidBody("POST", "/api/projects/1/budgets"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("project_completed", await CodeOf(response));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Price_changes_are_refused_without_catalog_approve(string decision)
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, ProjectResponsibilities.ProjectManager, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("catalog", "VCE")]);

        var response = await Send(app, "POST", $"/api/projects/1/price-changes/2/{decision}", token, new { note = "Checked with the supplier" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(database.Commands);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Price_changes_recheck_catalog_approve_in_the_database(string decision)
    {
        // The token says catalog Approve, but the database no longer does (the fake database has no such user):
        // refused before the price is read, and nothing is written.
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, ProjectResponsibilities.ProjectManager, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("catalog", "VCEA")]);

        var response = await Send(app, "POST", $"/api/projects/1/price-changes/2/{decision}", token, new { note = "Checked with the supplier" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("permission_revoked", await CodeOf(response));
        var command = Assert.Single(database.Commands);
        Assert.StartsWith("SELECT", command);
        Assert.DoesNotContain("[ItemPrice]", command);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("return")]
    public async Task Daily_reports_are_reviewed_by_an_executive_or_the_projects_manager_only(string action)
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, ProjectResponsibilities.SiteEngineer, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("site", "VCA")]);

        var response = await Send(app, "POST", $"/api/projects/1/daily-reports/2/{action}", token, new { note = "Add photos", rowVersion = RowVersion });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task A_completed_project_refuses_a_daily_report()
    {
        var (app, _) = await StartAsync(new VisibleProject(IsCompleted: true, ProjectResponsibilities.SiteEngineer, Executive: false));
        await using var __ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("site", "VC")]);

        var response = await Send(app, "POST", "/api/projects/1/daily-reports", token, ValidBody("POST", "/api/projects/1/daily-reports"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("project_completed", await CodeOf(response));
    }

    [Fact]
    public async Task Filing_a_report_nobody_asked_for_needs_reports_create_and_asking_needs_reports_edit()
    {
        await using var app = await TestApplication.StartAsync();
        var asker = TestTokens.Create(role: Manager, permissions: [("reports", "VE")]);
        var author = TestTokens.Create(role: "engineer", permissions: [("reports", "VC")]);

        var filing = await Send(app, "POST", "/api/projects/1/report-requests", asker, new
        {
            isProactive = true,
            sendToUserId = 2,
            type = "Materials",
            title = "Cement stock",
            responseText = "40 bags left",
        });
        var asking = await Send(app, "POST", "/api/projects/1/report-requests", author, ValidBody("POST", "/api/projects/1/report-requests"));

        Assert.Equal(HttpStatusCode.Forbidden, filing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, asking.StatusCode);
    }

    [Fact]
    public async Task A_filed_report_needs_its_text_and_no_recipient_or_due_date()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(userId: 7, role: "engineer", permissions: [("reports", "VC")]);

        var response = await Send(app, "POST", "/api/projects/1/report-requests", token, new
        {
            isProactive = true,
            recipientUserId = 2,
            sendToUserId = 7,
            type = "Other",
            title = "Site note",
            dueDate = "2030-01-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["dueDate", "recipientUserId", "responseText", "sendToUserId"], (await ErrorFields(response)).Order(StringComparer.Ordinal));
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task The_overview_and_reports_answer_with_no_cost_source_registered()
    {
        var (app, _) = await StartAsync(
            new VisibleProject(IsCompleted: false, ProjectResponsibilities.ProjectManager, Executive: false),
            removeCostSources: true);
        await using var __ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("projects", "VF")]);

        var overview = await Send(app, "GET", "/api/projects/1/overview", token);
        var summary = await Send(app, "GET", "/api/projects/1/reports/cost-summary?from=2026-01-01&to=2026-12-31", token);
        var obligations = await Send(app, "GET", "/api/projects/1/reports/obligations", token);
        var comparison = await Send(app, "GET", "/api/projects/1/budget-comparison", token);

        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal(HttpStatusCode.OK, obligations.StatusCode);
        Assert.Equal(HttpStatusCode.OK, comparison.StatusCode);

        var body = await Json(overview);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("budget").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("totals").GetProperty("remainingBudget").ValueKind);
        var groups = body.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(CostGroups.All, groups.Select(group => group.GetProperty("costGroup").GetString()));
        Assert.All(
            groups.Where(group => ProjectSourceGroups.Contains(group.GetProperty("costGroup").GetString())),
            group => Assert.False(group.GetProperty("hasSource").GetBoolean()));

        var owed = (await Json(obligations)).GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(4, owed.Count);
        Assert.All(owed, group => Assert.Equal(0m, group.GetProperty("outstanding").GetDecimal()));
    }

    [Fact]
    public async Task The_cost_summary_filters_the_records_by_their_business_date_in_sql()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, ProjectResponsibilities.ProjectManager, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("projects", "VF")]);

        var response = await Send(app, "GET", "/api/projects/1/reports/cost-summary?from=2026-01-01&to=2026-01-31", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sql = Assert.Single(database.Commands, command => command.Contains("[projects].[ProjectExpense]", StringComparison.Ordinal));
        Assert.Contains("[ExpenseDate] >= @", sql);
        Assert.Contains("[ExpenseDate] <= @", sql);
        Assert.Contains("[ProjectId] = @", sql);
    }

    private static readonly string[] ProjectSourceGroups = [CostGroups.Labor, CostGroups.Subcontractors, CostGroups.Equipment, CostGroups.Staff];

    /// <summary>A body that passes validation for each endpoint, so tests reach the checks after it.</summary>
    internal static object? ValidBody(string method, string path)
    {
        if (method == "GET")
        {
            return null;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var segments = path.Split('/');
        var last = segments[^1];

        if (path.Contains("/report-requests", StringComparison.Ordinal))
        {
            return last == "report-requests"
                ? new { recipientUserId = 2, type = "Materials", priority = "High", title = "Cement stock count", dueDate = Date(today.AddDays(10)) }
                : new { responseText = "40 bags in the store", note = "Count the steel too", rowVersion = RowVersion };
        }

        if (path.Contains("/daily-reports", StringComparison.Ordinal))
        {
            return last is "review" or "return"
                ? new { note = "Add photos of the slab", rowVersion = RowVersion }
                : new
                {
                    reportDate = Date(today.AddDays(-2)),
                    workersPresent = 14,
                    weather = "Sunny",
                    lines = new[] { new { workType = "Concrete", description = "Poured the ground floor slab", unit = "M3", quantity = 12.5m, performedBy = "Internal" } },
                    materials = new[] { new { materialName = "Cement", unit = "Bag", quantity = 80m } },
                    equipment = new[] { new { equipmentName = "Concrete pump", hours = 6.5m } },
                    rowVersion = RowVersion,
                };
        }

        if (path.Contains("/price-changes", StringComparison.Ordinal))
        {
            return last == "price-changes"
                ? new { price = 160m, effectiveFrom = Date(today.AddDays(5)), reason = "The supplier raised the price" }
                : new { note = "Checked with the supplier" };
        }

        if (path.Contains("/catalog-items", StringComparison.Ordinal))
        {
            return new
            {
                nameAr = "أسمنت بورتلاندي",
                nameEn = "Portland cement",
                category = "CementAggregates",
                unit = "Bag",
                price = 150m,
                estimatedQuantity = 1000m,
                status = "Active",
                rowVersion = RowVersion,
            };
        }

        if (last == "approve")
        {
            return new { rowVersion = RowVersion };
        }

        return new
        {
            title = "Main estimate",
            lines = new object[]
            {
                new { costGroup = "Materials", description = "Cement", unit = "Bag", quantity = 1000m, unitPrice = 150m },
                new { costGroup = "Labor", description = "Concrete crew", amount = 50_000m },
            },
            rowVersion = RowVersion,
        };
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Every permission of every module except the letters this endpoint needs.</summary>
    private static (string Module, string Letters)[] AllBut(string permission)
    {
        var (module, letters) = (permission.Split(':')[0], permission.Split(':')[1]);
        return Modules
            .Select(candidate => (candidate, candidate == module ? new string("VCEAF".Where(letter => !letters.Contains(letter)).ToArray()) : "VCEAF"))
            .ToArray();
    }

    /// <summary>Replaces <c>%T201%</c> with a text of 201 characters, so long texts stay readable in the data.</summary>
    private static string Expand(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "%T(\\d+)%", match => new string('x', int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));

    private static async Task<(TestApplication App, StandInDatabase Database)> StartAsync(IProjectAccess? access = null, bool removeCostSources = false)
    {
        var database = new StandInDatabase();
        var app = await TestApplication.StartAsync(configureBuilder: builder =>
        {
            database.AddTo(builder);
            if (access is not null)
            {
                builder.Services.AddScoped(_ => access);
            }

            if (removeCostSources)
            {
                builder.Services.RemoveAll<IProjectCostSource>();
            }
        });
        return (app, database);
    }

    /// <summary>Says every project is visible, so tests reach the rules that come after the scope check.</summary>
    private sealed record VisibleProject(bool IsCompleted, string? Responsibility, bool Executive) : IProjectAccess
    {
        public Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectAccessInfo?>(new ProjectAccessInfo(projectId, "P-1", "مشروع", "Project", IsCompleted, Responsibility, Executive));
    }
}
