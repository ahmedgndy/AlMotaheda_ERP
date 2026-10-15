using System.Net;
using System.Security.Claims;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Workforce.Tests.Api;

/// <summary>
/// Every Workforce endpoint over real HTTP without SQL Server: sign-in, permissions, project scope (404),
/// validation (400 with every failing field), and the rule checks that run before any save.
/// </summary>
public sealed class WorkforceApiSecurityTests
{
    private const string Executive = "executive";
    private const string Manager = "manager";
    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>Every endpoint, with the permission letters it needs (several letters: any one of them).</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/trades", "workers:V" },
        { "POST", "/api/trades", "workers:E" },
        { "PUT", "/api/trades/1", "workers:E" },
        { "GET", "/api/workers", "workers:V" },
        { "GET", "/api/workers/1", "workers:V" },
        { "POST", "/api/workers", "workers:E" },
        { "PUT", "/api/workers/1", "workers:E" },
        { "GET", "/api/workers/1/account", "workers:F" },
        { "GET", "/api/workers/1/payments", "workers:F" },
        { "POST", "/api/workers/1/payments", "payments:C" },
        { "POST", "/api/worker-payments/1/void", "payments:A" },
        { "GET", "/api/projects/1/workers", "workers:V" },
        { "POST", "/api/projects/1/workers", "workers:E" },
        { "POST", "/api/projects/1/workers/2/end", "workers:E" },
        { "GET", "/api/projects/1/labor/sheets", "workers:V" },
        { "GET", "/api/projects/1/labor/sheets/2", "workers:V" },
        { "POST", "/api/projects/1/labor/sheets", "workers:C" },
        { "PUT", "/api/projects/1/labor/sheets/2", "workers:C" },
        { "POST", "/api/projects/1/labor/sheets/2/submit", "workers:C" },
        { "POST", "/api/projects/1/labor/sheets/2/approve", "workers:A" },
        { "POST", "/api/projects/1/labor/sheets/2/return", "workers:A" },
        { "POST", "/api/projects/1/labor/sheets/2/reject", "workers:A" },
        { "PUT", "/api/projects/1/labor/sheets/2/allocations", "workers:CEA" },
        { "PUT", "/api/projects/1/labor/lines/3/allocations", "workers:EA" },
        { "GET", "/api/projects/1/labor/summary", "workers:V" },
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
        // Every other permission of the module, and everything of the other module, but not the letters this endpoint needs.
        var (module, letters) = (permission.Split(':')[0], permission.Split(':')[1]);
        var others = new string("VCEAF".Where(letter => !letters.Contains(letter)).ToArray());
        var token = TestTokens.Create(role: Manager, permissions: [(module, others), (module == "workers" ? "payments" : "workers", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_project_the_caller_cannot_see_answers_404_on_every_project_endpoint(string method, string path, string permission)
    {
        _ = permission;
        if (!path.StartsWith("/api/projects/", StringComparison.Ordinal))
        {
            return; // trades, workers and worker payments are company-wide
        }

        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("workers", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await CodeOf(response));
    }

    [Fact]
    public async Task Filtering_workers_by_a_project_the_caller_cannot_see_answers_404()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("workers", "VC")]);

        var response = await Send(app, "GET", "/api/workers?projectId=1", token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/trades", "code,defaultDailyRate,defaultWorkType,nameAr,nameEn")]
    [InlineData("PUT", "/api/trades/1", "code,defaultDailyRate,defaultWorkType,isActive,nameAr,nameEn,rowVersion")]
    [InlineData("POST", "/api/workers", "nameAr,nameEn,tradeId")]
    [InlineData("PUT", "/api/workers/1", "nameAr,nameEn,rowVersion,status,tradeId")]
    [InlineData("POST", "/api/workers/1/payments", "amount,idempotencyKey,method,paymentDate")]
    [InlineData("POST", "/api/worker-payments/1/void", "reason,rowVersion")]
    [InlineData("POST", "/api/projects/1/workers", "workerId")]
    [InlineData("POST", "/api/projects/1/workers/2/end", "rowVersion")]
    [InlineData("POST", "/api/projects/1/labor/sheets", "lines,shift,workDate")]
    [InlineData("PUT", "/api/projects/1/labor/sheets/2", "lines,rowVersion,shift,workDate")]
    [InlineData("POST", "/api/projects/1/labor/sheets/2/submit", "rowVersion")]
    [InlineData("POST", "/api/projects/1/labor/sheets/2/approve", "rowVersion")]
    [InlineData("POST", "/api/projects/1/labor/sheets/2/return", "note,rowVersion")]
    [InlineData("POST", "/api/projects/1/labor/sheets/2/reject", "note,rowVersion")]
    [InlineData("PUT", "/api/projects/1/labor/sheets/2/allocations", "lines,rowVersion")]
    [InlineData("PUT", "/api/projects/1/labor/lines/3/allocations", "allocations")]
    public async Task Empty_forms_list_their_required_fields(string method, string path, string fields)
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("workers", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
        Assert.Equal(fields.Split(','), (await ErrorFields(response)).Order(StringComparer.Ordinal));
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task A_sheet_form_reports_every_failing_field_before_any_database_work()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("workers", "VC")]);

        var response = await Send(app, "POST", "/api/projects/1/labor/sheets", token, new
        {
            workDate = "2020-01-01",
            shift = "Evening",
            lines = new object[]
            {
                new
                {
                    workerId = 1,
                    days = 2,
                    overtimeHours = 20,
                    adjustments = new[] { new { kind = "Deduction", type = "Other", amount = 50 } },
                    allocations = new[] { new { itemId = 4, portion = 1.5 } },
                },
                new { workerId = 1, days = 1 },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            [
                "lines[0].adjustments[0].reason",
                "lines[0].allocations[0].portion",
                "lines[0].days",
                "lines[0].overtimeHours",
                "lines[1].workerId",
                "shift",
                "workDate",
            ],
            (await ErrorFields(response)).Order(StringComparer.Ordinal));
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task A_worker_form_checks_the_national_id_phone_and_rate()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "accountant", permissions: [("workers", "VCEAF")]);

        var response = await Send(app, "POST", "/api/workers", token, new
        {
            nameAr = "أحمد علي",
            nameEn = "Ahmed Ali",
            nationalId = "2930412013707",
            phone = "call me",
            tradeId = 3,
            dailyRate = 400.001,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["dailyRate", "nationalId", "phone"], (await ErrorFields(response)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_trade_form_checks_the_code_and_the_work_type()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("workers", "VCEAF")]);

        var response = await Send(app, "POST", "/api/trades", token, new
        {
            code = "Scaffold Fixer!",
            nameAr = "سقالات",
            nameEn = "Scaffolder",
            defaultDailyRate = 0,
            defaultWorkType = "Climbing",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["code", "defaultDailyRate", "defaultWorkType"], (await ErrorFields(response)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/api/workers?status=Gone", "status")]
    [InlineData("/api/workers?tradeId=0", "tradeId")]
    [InlineData("/api/workers?projectId=0", "projectId")]
    [InlineData("/api/workers?pageSize=101", "pageSize")]
    [InlineData("/api/workers/1/payments?page=0", "page")]
    [InlineData("/api/projects/1/workers?pageSize=0", "pageSize")]
    [InlineData("/api/projects/1/labor/sheets?status=Paid", "status")]
    [InlineData("/api/projects/1/labor/sheets?shift=Evening", "shift")]
    [InlineData("/api/projects/1/labor/sheets?from=2026-13-01", "from")]
    [InlineData("/api/projects/1/labor/sheets?from=2026-02-01&to=2026-01-01", "to")]
    public async Task List_parameters_are_validated(string path, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("workers", "VCEAF")]);

        var response = await Send(app, "GET", path, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFields(response));
    }

    [Fact]
    public async Task Portions_above_one_day_are_refused_with_allocation_exceeds_day_before_any_database_read()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("workers", "VC")]);

        var response = await Send(app, "POST", "/api/projects/1/labor/sheets", token, SheetBody(
            new { workerId = 1, days = 1, allocations = new[] { new { itemId = 4, portion = 0.6 }, new { itemId = 5, portion = 0.5 } } }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("allocation_exceeds_day", await CodeOf(response));
        Assert.DoesNotContain(database.Commands, sql => sql.Contains("[workforce].[Worker]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_workers_and_items_are_reported_on_their_fields()
    {
        // The stand-in database knows no worker, trade or project item.
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("workers", "VC")]);

        var response = await Send(app, "POST", "/api/projects/1/labor/sheets", token, SheetBody(
            new { workerId = 1, days = 1, allocations = new[] { new { itemId = 4, portion = 1 } } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["lines[0].allocations[0].itemId", "lines[0].workerId"], (await ErrorFields(response)).Order(StringComparer.Ordinal));
        Assert.Contains(database.Commands, sql => sql.Contains("[workforce].[DailySheet]", StringComparison.Ordinal) && sql.Contains("[RecordedBy] = @", StringComparison.Ordinal));
        Assert.Contains(database.Commands, sql => sql.Contains("[projects].[ProjectItem]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/workers")]
    [InlineData("POST", "/api/projects/1/workers/2/end")]
    [InlineData("POST", "/api/projects/1/labor/sheets")]
    [InlineData("PUT", "/api/projects/1/labor/sheets/2")]
    [InlineData("POST", "/api/projects/1/labor/sheets/2/submit")]
    [InlineData("POST", "/api/projects/1/labor/sheets/2/approve")]
    [InlineData("POST", "/api/projects/1/labor/sheets/2/reject")]
    [InlineData("PUT", "/api/projects/1/labor/sheets/2/allocations")]
    [InlineData("PUT", "/api/projects/1/labor/lines/3/allocations")]
    public async Task A_completed_project_takes_no_change_from_anyone_but_an_executive(string method, string path)
    {
        var (app, _) = await StartAsync(new VisibleProject(IsCompleted: true, Executive: false));
        await using var __ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("workers", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("project_completed", await CodeOf(response));
    }

    [Fact]
    public async Task An_executive_gets_past_the_completed_check_to_the_next_rule()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: true, Executive: true));
        await using var _ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("workers", "VCEAF")]);

        // The worker lookup runs on the stand-in database, which knows no worker.
        var response = await Send(app, "POST", "/api/projects/1/workers", token, new { workerId = 9 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["workerId"], await ErrorFields(response));
        Assert.Contains(database.Commands, sql => sql.Contains("[workforce].[Worker]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reads_of_a_project_send_the_project_id_in_the_sql_and_hide_money_without_see_money()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Executive: false));
        await using var _ = app;
        var engineer = TestTokens.Create(role: "engineer", permissions: [("workers", "VC")]);

        var summary = await Send(app, "GET", "/api/projects/1/labor/summary", engineer);
        var sheets = await Send(app, "GET", "/api/projects/1/labor/sheets?mine=true&status=Approved", engineer);
        var workers = await Send(app, "GET", "/api/projects/1/workers?includeEnded=true", engineer);
        var missing = await Send(app, "GET", "/api/projects/1/labor/sheets/2", engineer);

        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sheets.StatusCode);
        Assert.Equal(HttpStatusCode.OK, workers.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var body = await Json(summary);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("approved").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("pendingAmount").ValueKind);
        Assert.Equal(0, body.GetProperty("workersAssigned").GetInt32());

        Assert.All(
            database.Commands.Where(sql => sql.Contains("[workforce].[DailySheet]", StringComparison.Ordinal)),
            sql => Assert.Contains("[ProjectId] = @", sql));
        Assert.Contains(database.Commands, sql => sql.Contains("[RecordedBy] = @", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_summary_with_see_money_shows_zero_amounts_for_an_empty_project()
    {
        var (app, _) = await StartAsync(new VisibleProject(IsCompleted: false, Executive: true));
        await using var __ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("workers", "VCEAF")]);

        var body = await Json(await Send(app, "GET", "/api/projects/1/labor/summary", token));

        Assert.Equal(0m, body.GetProperty("approved").GetDecimal());
        Assert.Equal(0m, body.GetProperty("paid").GetDecimal());
        Assert.Equal(0m, body.GetProperty("notLinkedCost").GetDecimal());
        Assert.Equal(0m, body.GetProperty("pendingAmount").GetDecimal());
    }

    [Fact]
    public async Task Worker_search_text_is_sent_as_a_parameter_never_inside_the_sql()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("workers", "VE")]);

        var response = await Send(app, "GET", "/api/workers?search=x'%20OR%201=1--&status=active", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(database.Commands, sql => Assert.DoesNotContain("OR 1=1", sql));
    }

    [Fact]
    public async Task Missing_company_records_answer_404()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("workers", "VCEAF"), ("payments", "VCEAF")]);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(app, "GET", "/api/workers/1", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(app, "GET", "/api/workers/1/account", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(app, "GET", "/api/workers/1/payments", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(app, "PUT", "/api/trades/1", token, ValidBody("PUT", "/api/trades/1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(app, "PUT", "/api/workers/1", token, ValidBody("PUT", "/api/workers/1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(app, "POST", "/api/worker-payments/1/void", token, ValidBody("POST", "/api/worker-payments/1/void"))).StatusCode);
    }

    /// <summary>A body that passes validation for each endpoint, so tests reach the checks after it.</summary>
    internal static object? ValidBody(string method, string path)
    {
        if (method == "GET")
        {
            return null;
        }

        return path switch
        {
            _ when path.StartsWith("/api/trades", StringComparison.Ordinal) =>
                new { code = "scaffolder", nameAr = "سقالات", nameEn = "Scaffolder", defaultDailyRate = 400, defaultWorkType = "General", isActive = true, rowVersion = RowVersion },
            _ when path.EndsWith("/payments", StringComparison.Ordinal) =>
                new { paymentDate = "2026-01-20", amount = 1000, method = "Cash", idempotencyKey = Guid.NewGuid().ToString() },
            _ when path.EndsWith("/void", StringComparison.Ordinal) => new { reason = "Entered twice", rowVersion = RowVersion },
            _ when path.StartsWith("/api/workers", StringComparison.Ordinal) =>
                new { nameAr = "أحمد علي", nameEn = "Ahmed Ali", nationalId = "29304120137073", tradeId = 3, status = "Active", rowVersion = RowVersion },
            _ when path.EndsWith("/end", StringComparison.Ordinal) => new { rowVersion = RowVersion },
            _ when path.EndsWith("/workers", StringComparison.Ordinal) => new { workerId = 9, dailyRate = 450 },
            _ when path.Contains("/lines/", StringComparison.Ordinal) => new { allocations = new[] { new { itemId = 4, portion = 1 } } },
            _ when path.EndsWith("/allocations", StringComparison.Ordinal) =>
                new { rowVersion = RowVersion, lines = new[] { new { lineId = 3, allocations = new[] { new { itemId = 4, portion = 1 } } } } },
            _ when path.EndsWith("/submit", StringComparison.Ordinal) || path.EndsWith("/approve", StringComparison.Ordinal) =>
                new { rowVersion = RowVersion },
            _ when path.EndsWith("/return", StringComparison.Ordinal) || path.EndsWith("/reject", StringComparison.Ordinal) =>
                new { note = "Worker 7 was on leave", rowVersion = RowVersion },
            _ => SheetBody(new { workerId = 9, days = 1 }),
        };
    }

    private static object SheetBody(params object[] lines) => new
    {
        workDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        shift = "Day",
        lines,
        rowVersion = RowVersion,
    };

    private static async Task<(TestApplication App, StandInDatabase Database)> StartAsync(IProjectAccess? access = null)
    {
        var database = new StandInDatabase();
        var app = await TestApplication.StartAsync(configureBuilder: builder =>
        {
            database.AddTo(builder);
            if (access is not null)
            {
                builder.Services.AddScoped(_ => access);
            }
        });
        return (app, database);
    }

    /// <summary>Says every project is visible, so tests reach the rules that come after the scope check.</summary>
    private sealed record VisibleProject(bool IsCompleted, bool Executive) : IProjectAccess
    {
        public Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectAccessInfo?>(new ProjectAccessInfo(projectId, "P-1", "مشروع", "Project", IsCompleted, ProjectResponsibilities.SiteEngineer, Executive));
    }
}
