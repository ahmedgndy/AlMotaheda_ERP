using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Subcontractors.Tests.Api;

/// <summary>
/// Every Subcontractors endpoint over real HTTP without SQL Server: sign-in (401), permissions (403), project
/// scope and child records of another project (404), validation (400, every failing field), and the rule checks
/// that run before any save.
/// </summary>
public sealed class SubcontractorsApiSecurityTests
{
    private const string Executive = "executive";
    private const string Manager = "manager";
    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>Every endpoint of the module, with the permission it needs (two letters = either one).</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/subcontractors", "subcontractors:V" },
        { "GET", "/api/subcontractors/1", "subcontractors:V" },
        { "POST", "/api/subcontractors", "subcontractors:C" },
        { "PUT", "/api/subcontractors/1", "subcontractors:E" },
        { "GET", "/api/projects/1/subcontracts", "subcontractors:V" },
        { "GET", "/api/projects/1/subcontracts/2", "subcontractors:V" },
        { "POST", "/api/projects/1/subcontracts", "subcontractors:C" },
        { "PUT", "/api/projects/1/subcontracts/2", "subcontractors:E" },
        { "POST", "/api/projects/1/subcontracts/2/items", "subcontractors:CE" },
        { "PUT", "/api/projects/1/subcontracts/2/items/3", "subcontractors:CE" },
        { "DELETE", "/api/projects/1/subcontracts/2/items/3", "subcontractors:E" },
        { "PUT", "/api/projects/1/subcontracts/2/items/3/project-item", "subcontractors:E" },
        { "POST", "/api/projects/1/subcontracts/2/measurements", "subcontractors:CE" },
        { "POST", "/api/projects/1/subcontract-measurements/4/approve", "subcontractors:A" },
        { "POST", "/api/projects/1/subcontract-measurements/4/reject", "subcontractors:A" },
        { "POST", "/api/projects/1/subcontracts/2/manpower", "subcontractors:C" },
        { "POST", "/api/projects/1/subcontract-manpower/4/approve", "subcontractors:A" },
        { "POST", "/api/projects/1/subcontract-manpower/4/reject", "subcontractors:A" },
        { "PUT", "/api/projects/1/subcontract-manpower/4/project-item", "subcontractors:E" },
        { "POST", "/api/projects/1/subcontracts/2/payments", "payments:C" },
        { "POST", "/api/projects/1/subcontract-payments/5/void", "payments:A" },
        { "GET", "/api/subcontractors/1/account", "subcontractors:F" },
        { "POST", "/api/projects/1/subcontracts/2/variations", "subcontractors:E" },
        { "POST", "/api/projects/1/subcontract-variations/6/approve", "subcontractors:A" },
        { "POST", "/api/projects/1/subcontract-variations/6/reject", "subcontractors:A" },
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
        // Every other permission of both modules, but not the one(s) this endpoint needs.
        var (module, letters) = (permission.Split(':')[0], permission.Split(':')[1]);
        var others = new string("VCEAF".Where(letter => !letters.Contains(letter)).ToArray());
        var token = TestTokens.Create(role: Manager, permissions: [(module, others), (module == "payments" ? "subcontractors" : "payments", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_project_the_caller_cannot_see_answers_404_on_every_project_endpoint(string method, string path, string permission)
    {
        _ = permission;
        if (path.StartsWith("/api/subcontractors", StringComparison.Ordinal))
        {
            return; // companies are company-wide
        }

        var (app, database) = await StartAsync();
        await using var host = app;
        var token = TestTokens.Create(role: Manager, permissions: [("subcontractors", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await CodeOf(response));
        Assert.DoesNotContain(database.Commands, sql => sql.Contains("[sub].", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("GET", "/api/projects/1/subcontracts/2")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/items")]
    [InlineData("PUT", "/api/projects/1/subcontracts/2/items/3/project-item")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/measurements")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/manpower")]
    [InlineData("PUT", "/api/projects/1/subcontract-manpower/4/project-item")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/payments")]
    public async Task A_child_record_that_is_not_on_this_project_answers_404(string method, string path)
    {
        // The project is visible, but the stand-in database has no contract, item or sheet with that id on it.
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Executive: true));
        await using var host = app;
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await CodeOf(response));

        // The child is always looked up together with the project id of the route.
        var lookups = database.Commands.Where(sql => sql.Contains("[sub].", StringComparison.Ordinal) && !sql.Contains("[IdempotencyKey]", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lookups);
        Assert.All(lookups, sql => Assert.Contains("[ProjectId] = @", sql));
    }

    [Theory]
    [InlineData("GET", "/api/subcontractors/1")]
    [InlineData("PUT", "/api/subcontractors/1")]
    public async Task A_company_that_does_not_exist_answers_404(string method, string path)
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/subcontracts/2/items", "C")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/items", "E")]
    [InlineData("PUT", "/api/projects/1/subcontracts/2/items/3", "C")]
    [InlineData("PUT", "/api/projects/1/subcontracts/2/items/3", "E")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/measurements", "C")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/measurements", "E")]
    public async Task Items_and_measurements_take_subcontractors_create_or_edit(string method, string path, string letter)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Manager, permissions: [("subcontractors", letter)]);

        // An empty form: reaching validation proves the permission check let the caller through.
        var response = await Send(app, method, path, token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/subcontractors", "nameAr,nameEn,specialty")]
    [InlineData("PUT", "/api/subcontractors/1", "nameAr,nameEn,rowVersion,specialty,status")]
    [InlineData("POST", "/api/projects/1/subcontracts", "model,startDate,subcontractorId,value")]
    [InlineData("PUT", "/api/projects/1/subcontracts/2", "model,rowVersion,startDate,value")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/items", "name,quantity,unit,unitPrice,workType")]
    [InlineData("PUT", "/api/projects/1/subcontracts/2/items/3", "name,quantity,rowVersion,unit,unitPrice,workType")]
    [InlineData("PUT", "/api/projects/1/subcontracts/2/items/3/project-item", "rowVersion")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/measurements", "contractItemId,measuredOn,quantity")]
    [InlineData("POST", "/api/projects/1/subcontract-measurements/4/approve", "rowVersion")]
    [InlineData("POST", "/api/projects/1/subcontract-measurements/4/reject", "note,rowVersion")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/manpower", "days,fromDate,toDate,workers,workType")]
    [InlineData("POST", "/api/projects/1/subcontract-manpower/4/approve", "rowVersion")]
    [InlineData("POST", "/api/projects/1/subcontract-manpower/4/reject", "note,rowVersion")]
    [InlineData("PUT", "/api/projects/1/subcontract-manpower/4/project-item", "rowVersion")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/payments", "amount,idempotencyKey,method,paymentDate,purpose")]
    [InlineData("POST", "/api/projects/1/subcontract-payments/5/void", "reason,rowVersion")]
    public async Task Empty_forms_list_their_required_fields_before_any_database_work(string method, string path, string fields)
    {
        var (app, database) = await StartAsync();
        await using var host = app;
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
        Assert.Equal(fields.Split(','), (await ErrorFields(response)).Order());
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task A_company_form_reports_every_bad_field_at_once()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "C")]);

        var response = await Send(app, "POST", "/api/subcontractors", token, new
        {
            nameAr = new string('ا', 201),
            nameEn = "ABC Construction",
            specialty = "Plumbers",
            phone = "call me",
            email = "not-an-email",
            address = new string('x', 401),
            status = "Pending",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["address", "email", "nameAr", "phone", "specialty", "status"], (await ErrorFields(response)).Order());
    }

    [Theory]
    [InlineData("UnitRate", 400, null, "dailyRate")]
    [InlineData("DailyRate", null, null, "dailyRate")]
    [InlineData("LumpSum", null, 101.0, "retentionPercent")]
    [InlineData("LumpSum", null, 2.555, "retentionPercent")]
    public async Task A_contract_form_checks_the_daily_rate_against_the_model_and_the_retention(string model, int? dailyRate, double? retention, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "C")]);

        var response = await Send(app, "POST", "/api/projects/1/subcontracts", token, new
        {
            subcontractorId = 1,
            model,
            value = 100_000,
            dailyRate,
            retentionPercent = retention,
            startDate = "2026-06-01",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([field], await ErrorFields(response));
    }

    [Fact]
    public async Task A_contract_cannot_end_before_it_starts()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "C")]);

        var response = await Send(app, "POST", "/api/projects/1/subcontracts", token, new
        {
            subcontractorId = 1, model = "UnitRate", value = 100_000.001, startDate = "2026-06-01", endDate = "2026-05-31", status = "Signed",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["endDate", "status", "value"], (await ErrorFields(response)).Order());
    }

    [Theory]
    [InlineData(5.0, 10.0, "quantity")]
    [InlineData(null, 100.5, "percentOfLine")]
    [InlineData(null, 0.0, "percentOfLine")]
    [InlineData(1.0001, null, "quantity")]
    public async Task A_measurement_takes_either_a_quantity_or_a_percentage_of_the_line(double? quantity, double? percent, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Manager, permissions: [("subcontractors", "C")]);

        var response = await Send(app, "POST", "/api/projects/1/subcontracts/2/measurements", token, new
        {
            contractItemId = 3, measuredOn = "2026-01-10", quantity, percentOfLine = percent,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([field], await ErrorFields(response));
    }

    [Fact]
    public async Task Work_and_payments_cannot_be_dated_in_the_future()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "VCEAF"), ("payments", "VCEAF")]);

        var measurement = await Send(app, "POST", "/api/projects/1/subcontracts/2/measurements", token, new { contractItemId = 3, measuredOn = "2999-01-01", quantity = 1 });
        var manpower = await Send(app, "POST", "/api/projects/1/subcontracts/2/manpower", token, new
        {
            fromDate = "2026-01-05", toDate = "2999-01-01", workers = 3, days = 2, workType = "Plastering",
        });
        var payment = await Send(app, "POST", "/api/projects/1/subcontracts/2/payments", token, new
        {
            paymentDate = "2999-01-01", amount = 100, method = "Cash", purpose = "Advance", idempotencyKey = Guid.NewGuid(),
        });

        Assert.Equal(["measuredOn"], await ErrorFields(measurement));
        Assert.Equal(["toDate"], await ErrorFields(manpower));
        Assert.Equal(["paymentDate"], await ErrorFields(payment));
    }

    [Fact]
    public async Task A_manpower_form_reports_every_bad_field_at_once()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Manager, permissions: [("subcontractors", "C")]);

        var response = await Send(app, "POST", "/api/projects/1/subcontracts/2/manpower", token, new
        {
            fromDate = "2026-01-10", toDate = "2026-01-05", workers = 0, days = 0.0001, dailyRate = -400, workType = "Cooking", projectItemId = 0,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["dailyRate", "days", "projectItemId", "toDate", "workers", "workType"], (await ErrorFields(response)).Order());
    }

    [Fact]
    public async Task A_payment_form_reports_every_bad_field_at_once()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "accountant", permissions: [("payments", "C")]);

        var response = await Send(app, "POST", "/api/projects/1/subcontracts/2/payments", token, new
        {
            paymentDate = "2026-13-01", amount = 100.001, method = "Crypto", purpose = "Bonus", reference = new string('r', 101), idempotencyKey = "not-a-guid",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["amount", "idempotencyKey", "method", "paymentDate", "purpose", "reference"], (await ErrorFields(response)).Order());
    }

    [Theory]
    [InlineData("/api/subcontractors?specialty=Food", "specialty")]
    [InlineData("/api/subcontractors?status=Gone", "status")]
    [InlineData("/api/subcontractors?pageSize=101", "pageSize")]
    [InlineData("/api/subcontractors?page=0", "page")]
    [InlineData("/api/projects/1/subcontracts?status=Signed", "status")]
    public async Task List_parameters_are_validated(string path, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "VCEAF")]);

        var response = await Send(app, "GET", path, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFields(response));
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/subcontracts")]
    [InlineData("PUT", "/api/projects/1/subcontracts/2")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/items")]
    [InlineData("PUT", "/api/projects/1/subcontracts/2/items/3")]
    [InlineData("DELETE", "/api/projects/1/subcontracts/2/items/3")]
    [InlineData("PUT", "/api/projects/1/subcontracts/2/items/3/project-item")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/measurements")]
    [InlineData("POST", "/api/projects/1/subcontract-measurements/4/approve")]
    [InlineData("POST", "/api/projects/1/subcontract-measurements/4/reject")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/manpower")]
    [InlineData("POST", "/api/projects/1/subcontract-manpower/4/approve")]
    [InlineData("PUT", "/api/projects/1/subcontract-manpower/4/project-item")]
    [InlineData("POST", "/api/projects/1/subcontracts/2/payments")]
    [InlineData("POST", "/api/projects/1/subcontract-payments/5/void")]
    public async Task A_completed_project_takes_no_change_from_anyone_but_an_executive(string method, string path)
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: true, Executive: false));
        await using var host = app;
        var token = TestTokens.Create(role: Manager, permissions: [("subcontractors", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("project_completed", await CodeOf(response));
        Assert.DoesNotContain(database.Commands, sql => sql.Contains("[sub].", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_executive_gets_past_the_completed_check_to_the_next_rule()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: true, Executive: true));
        await using var host = app;
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "VCEAF")]);

        // The company lookup runs on the stand-in database, which knows no company.
        var response = await Send(app, "POST", "/api/projects/1/subcontracts", token, ValidBody("POST", "/api/projects/1/subcontracts"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["subcontractorId"], await ErrorFields(response));
        Assert.Contains(database.Commands, sql => sql.Contains("[sub].[Subcontractor]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_contract_list_reads_one_project_and_hides_money_without_see_money()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Executive: false));
        await using var host = app;
        var withMoney = TestTokens.Create(role: Manager, permissions: [("subcontractors", "VF")]);
        var withoutMoney = TestTokens.Create(role: Manager, permissions: [("subcontractors", "V")]);

        var shown = await Json(await Send(app, "GET", "/api/projects/1/subcontracts?status=active", withMoney));
        var hidden = await Json(await Send(app, "GET", "/api/projects/1/subcontracts", withoutMoney));

        Assert.Equal(0m, shown.GetProperty("totals").GetProperty("executed").GetDecimal());
        Assert.Equal(0, shown.GetProperty("totals").GetProperty("contractCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("totals").ValueKind);
        Assert.Equal(0, hidden.GetProperty("items").GetArrayLength());

        var reads = database.Commands.Where(sql => sql.Contains("[sub].", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(reads);
        Assert.All(reads, sql => Assert.Contains("[ProjectId] = @", sql));
    }

    [Fact]
    public async Task Company_search_text_is_sent_as_a_parameter_never_inside_the_sql()
    {
        var (app, database) = await StartAsync();
        await using var host = app;
        var token = TestTokens.Create(role: Executive, permissions: [("subcontractors", "V")]);

        var response = await Send(app, "GET", "/api/subcontractors?search=x'%20OR%201=1--&specialty=civil&status=active", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(database.Commands, sql => Assert.DoesNotContain("OR 1=1", sql));
    }

    /// <summary>A body that passes validation for each endpoint, so tests reach the checks after it.</summary>
    internal static object? ValidBody(string method, string path)
    {
        if (method is "GET" or "DELETE")
        {
            return null;
        }

        if (path.EndsWith("/void", StringComparison.Ordinal))
        {
            return new { reason = "Entered twice", rowVersion = RowVersion };
        }

        if (path.EndsWith("/approve", StringComparison.Ordinal) || path.EndsWith("/reject", StringComparison.Ordinal))
        {
            return new { note = "Checked on site", rowVersion = RowVersion };
        }

        if (path.EndsWith("/project-item", StringComparison.Ordinal))
        {
            return new { projectItemId = 9, rowVersion = RowVersion };
        }

        if (path.EndsWith("/payments", StringComparison.Ordinal))
        {
            return new { paymentDate = "2026-01-20", amount = 1000, method = "BankTransfer", purpose = "Progress", reference = "TRF-1", idempotencyKey = Guid.NewGuid() };
        }

        if (path.EndsWith("/variations", StringComparison.Ordinal))
        {
            return new { amount = 50_000, reason = "Extra slab approved by the client", reference = "VO-1" };
        }

        if (path.EndsWith("/manpower", StringComparison.Ordinal))
        {
            return new { fromDate = "2026-01-05", toDate = "2026-01-08", workers = 16, days = 3, workType = "Plastering", projectItemId = 9 };
        }

        if (path.EndsWith("/measurements", StringComparison.Ordinal))
        {
            return new { contractItemId = 3, measuredOn = "2026-01-10", quantity = 45, note = "Floor 6 slab" };
        }

        if (path.Contains("/items", StringComparison.Ordinal))
        {
            return new { name = "Reinforcement steel", workType = "SteelFixing", unit = "Ton", quantity = 10, unitPrice = 40_000, rowVersion = RowVersion };
        }

        if (path.StartsWith("/api/subcontractors", StringComparison.Ordinal))
        {
            return new { nameAr = "إيه بي سي للإنشاءات", nameEn = "ABC Construction", specialty = "Civil", status = "Active", rowVersion = RowVersion };
        }

        return new
        {
            subcontractorId = 1,
            model = "UnitRate",
            value = 1_000_000,
            retentionPercent = 5,
            startDate = "2026-06-01",
            endDate = "2026-11-30",
            status = "Active",
            contractNo = "SC/2026/011-07",
            rowVersion = RowVersion,
        };
    }

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
            Task.FromResult<ProjectAccessInfo?>(new ProjectAccessInfo(projectId, "P-1", "مشروع", "Project", IsCompleted, null, Executive));
    }
}
