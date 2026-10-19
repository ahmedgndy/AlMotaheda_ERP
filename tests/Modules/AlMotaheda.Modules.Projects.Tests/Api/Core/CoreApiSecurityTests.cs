using System.Net;
using System.Security.Claims;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Projects.Tests.Api.Core;

/// <summary>
/// The suppliers, items, expenses and supplier payments endpoints over real HTTP without SQL Server: sign-in,
/// permissions, project scope (404), validation (400), and the rule checks that run before any save.
/// </summary>
public sealed class CoreApiSecurityTests
{
    private const string Executive = "executive";
    private const string Manager = "manager";

    /// <summary>Every endpoint of this part, with a body that passes validation (so scope and rules are reached).</summary>
    public static TheoryData<string, string, string> Endpoints => new()
    {
        { "GET", "/api/suppliers", "projects:V" },
        { "POST", "/api/suppliers", "projects:C" },
        { "PUT", "/api/suppliers/1", "projects:E" },
        { "GET", "/api/projects/1/items", "projects:F" },
        { "GET", "/api/projects/1/items/unallocated", "projects:F" },
        { "GET", "/api/projects/1/items/options", "projects:V" },
        { "GET", "/api/projects/1/items/2", "projects:F" },
        { "GET", "/api/projects/1/items/2/cost-breakdown", "projects:F" },
        { "PUT", "/api/projects/1/items/2/parent", "projects:E" },
        { "POST", "/api/projects/1/items", "projects:E" },
        { "PUT", "/api/projects/1/items/2", "projects:E" },
        { "POST", "/api/projects/1/items/2/deactivate", "projects:E" },
        { "POST", "/api/projects/1/items/2/reactivate", "projects:E" },
        { "DELETE", "/api/projects/1/items/2", "projects:E" },
        { "GET", "/api/projects/1/expenses", "projects:V" },
        { "GET", "/api/projects/1/expenses/2", "projects:V" },
        { "POST", "/api/projects/1/expenses", "projects:C" },
        { "PUT", "/api/projects/1/expenses/2", "projects:C" },
        { "POST", "/api/projects/1/expenses/2/approve", "projects:A" },
        { "POST", "/api/projects/1/expenses/2/return", "projects:A" },
        { "POST", "/api/projects/1/expenses/2/reject", "projects:A" },
        { "PUT", "/api/projects/1/expenses/2/item", "projects:EA" },
        { "POST", "/api/projects/1/expenses/2/adjustments", "projects:C" },
        { "GET", "/api/projects/1/supplier-payments", "projects:F" },
        { "POST", "/api/projects/1/supplier-payments", "payments:C" },
        { "POST", "/api/projects/1/supplier-payments/2/void", "payments:A" },
        { "GET", "/api/projects/1/supplier-balances", "projects:F" },
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
        var token = TestTokens.Create(role: Manager, permissions: [(module, others), (module == "projects" ? "payments" : "projects", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_project_the_caller_cannot_see_answers_404_on_every_project_endpoint(string method, string path, string permission)
    {
        _ = permission;
        if (path.StartsWith("/api/suppliers", StringComparison.Ordinal))
        {
            return; // suppliers are company-wide
        }

        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("projects", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await CodeOf(response));
    }

    [Fact]
    public async Task Linking_needs_projects_edit_or_approve()
    {
        await using var app = await TestApplication.StartAsync();
        var viewer = TestTokens.Create(role: Manager, permissions: [("projects", "VCF")]);
        var approver = TestTokens.Create(role: Manager, permissions: [("projects", "A")]);

        var refused = await Send(app, "PUT", "/api/projects/1/expenses/2/item", viewer, new { itemId = 3, rowVersion = RowVersion });
        var reached = await Send(app, "PUT", "/api/projects/1/expenses/2/item", approver, new { itemId = 0 });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reached.StatusCode);
        Assert.Equal(["itemId", "rowVersion"], (await ErrorFields(reached)).Order());
    }

    [Fact]
    public async Task A_supply_form_reports_every_failing_field_before_any_database_work()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("projects", "VC")]);

        var response = await Send(app, "POST", "/api/projects/1/expenses", token, new
        {
            kind = "Supply",
            expenseDate = "2999-01-01",
            unit = "Bag",
            quantity = -1,
            channel = "Credit",
            notes = new string('x', 1001),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
        Assert.Equal(
            ["expenseDate", "invoiceNo", "itemName", "notes", "quantity", "supplierId", "unitPrice"],
            (await ErrorFields(response)).Order());
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task A_payment_form_reports_every_failing_field_before_any_database_work()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: "accountant", permissions: [("payments", "C")]);

        var response = await Send(app, "POST", "/api/projects/1/supplier-payments", token, new
        {
            amount = 100.001,
            method = "Crypto",
            idempotencyKey = "not-a-guid",
            allocations = new[] { new { expenseId = 1, amount = 50 }, new { expenseId = 1, amount = 50 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            ["allocations[1]", "amount", "idempotencyKey", "method", "paymentDate", "supplierId"],
            (await ErrorFields(response)).Order());
        Assert.Empty(database.Commands);
    }

    [Fact]
    public async Task Allocations_that_do_not_add_up_to_the_payment_are_refused()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "accountant", permissions: [("payments", "C")]);

        var response = await Send(app, "POST", "/api/projects/1/supplier-payments", token, PaymentBody(amount: 1000, allocated: 999.99m));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["allocations"], await ErrorFields(response));
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/items", "category,nameAr,nameEn,unit")]
    [InlineData("PUT", "/api/projects/1/items/2", "category,nameAr,nameEn,rowVersion,unit")]
    [InlineData("POST", "/api/projects/1/items/2/deactivate", "rowVersion")]
    [InlineData("POST", "/api/suppliers", "kind,nameAr,nameEn")]
    [InlineData("PUT", "/api/suppliers/1", "kind,nameAr,nameEn,rowVersion,status")]
    [InlineData("POST", "/api/projects/1/expenses/2/approve", "rowVersion")]
    [InlineData("POST", "/api/projects/1/expenses/2/return", "note,rowVersion")]
    [InlineData("POST", "/api/projects/1/expenses/2/reject", "note,rowVersion")]
    [InlineData("POST", "/api/projects/1/supplier-payments/2/void", "reason,rowVersion")]
    public async Task Empty_forms_list_their_required_fields(string method, string path, string fields)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(fields.Split(','), (await ErrorFields(response)).Order());
    }

    [Theory]
    [InlineData("/api/suppliers?status=Gone", "status")]
    [InlineData("/api/suppliers?kind=Food", "kind")]
    [InlineData("/api/suppliers?pageSize=101", "pageSize")]
    [InlineData("/api/projects/1/expenses?kind=Rent", "kind")]
    [InlineData("/api/projects/1/expenses?status=Paid", "status")]
    [InlineData("/api/projects/1/expenses?channel=Card", "channel")]
    [InlineData("/api/projects/1/expenses?paymentState=Late", "paymentState")]
    [InlineData("/api/projects/1/expenses?from=2026-13-01", "from")]
    [InlineData("/api/projects/1/expenses?from=2026-02-01&to=2026-01-01", "to")]
    [InlineData("/api/projects/1/expenses?sort=supplier", "sort")]
    [InlineData("/api/projects/1/expenses?itemId=3&unlinked=true", "unlinked")]
    [InlineData("/api/projects/1/expenses?page=0", "page")]
    [InlineData("/api/projects/1/supplier-payments?supplierId=0", "supplierId")]
    public async Task List_parameters_are_validated(string path, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF")]);

        var response = await Send(app, "GET", path, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFields(response));
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/items")]
    [InlineData("PUT", "/api/projects/1/items/2")]
    [InlineData("POST", "/api/projects/1/items/2/deactivate")]
    [InlineData("POST", "/api/projects/1/items/2/reactivate")]
    [InlineData("DELETE", "/api/projects/1/items/2")]
    public async Task Only_an_executive_or_the_projects_manager_changes_its_items(string method, string path)
    {
        // A manager with every permission who is on the project as a team member, not as its manager.
        var (app, _) = await StartAsync(new VisibleProject(IsCompleted: false, Responsibility: ProjectResponsibilities.TeamMember, Executive: false));
        await using var __ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("projects", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Theory]
    [InlineData("POST", "/api/projects/1/items")]
    [InlineData("POST", "/api/projects/1/expenses")]
    [InlineData("PUT", "/api/projects/1/expenses/2")]
    [InlineData("POST", "/api/projects/1/expenses/2/approve")]
    [InlineData("PUT", "/api/projects/1/expenses/2/item")]
    [InlineData("POST", "/api/projects/1/supplier-payments")]
    [InlineData("POST", "/api/projects/1/supplier-payments/2/void")]
    public async Task A_completed_project_takes_no_change_from_anyone_but_an_executive(string method, string path)
    {
        var (app, _) = await StartAsync(new VisibleProject(IsCompleted: true, Responsibility: ProjectResponsibilities.ProjectManager, Executive: false));
        await using var __ = app;
        var token = TestTokens.Create(role: Manager, permissions: [("projects", "VCEAF"), ("payments", "VCEAF")]);

        var response = await Send(app, method, path, token, ValidBody(method, path));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("project_completed", await CodeOf(response));
    }

    [Fact]
    public async Task An_executive_gets_past_the_completed_check_to_the_next_rule()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: true, Responsibility: null, Executive: true));
        await using var _ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF"), ("payments", "VCEAF")]);

        // The supplier lookup runs on the stand-in database, which knows no supplier.
        var response = await Send(app, "POST", "/api/projects/1/supplier-payments", token, PaymentBody(1000, 1000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["supplierId"], await ErrorFields(response));
        Assert.Contains(database.Commands, sql => sql.Contains("[projects].[Supplier]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Records_of_a_project_are_read_with_the_project_id_in_the_sql()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Responsibility: null, Executive: true));
        await using var _ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF")]);

        var items = await Send(app, "GET", "/api/projects/1/items?search=x&includeInactive=true", token);
        var unallocated = await Send(app, "GET", "/api/projects/1/items/unallocated", token);
        var expenses = await Send(app, "GET", "/api/projects/1/expenses?paymentState=PartiallyPaid&search=cement", token);
        var payments = await Send(app, "GET", "/api/projects/1/supplier-payments?includeVoided=true", token);
        var balances = await Send(app, "GET", "/api/projects/1/supplier-balances", token);
        var missingItem = await Send(app, "GET", "/api/projects/1/items/2", token);
        var missingExpense = await Send(app, "GET", "/api/projects/1/expenses/2", token);

        foreach (var response in new[] { items, unallocated, expenses, payments, balances })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, missingItem.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingExpense.StatusCode);

        var body = await Json(items);
        Assert.Equal(0m, body.GetProperty("totals").GetProperty("totalCost").GetDecimal());
        Assert.Equal(0m, body.GetProperty("reconciliation").GetProperty("projectRecognizedCost").GetDecimal());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, (await Json(expenses)).GetProperty("summary").ValueKind);

        Assert.All(
            database.Commands.Where(sql => sql.Contains("[projects].[ProjectExpense]", StringComparison.Ordinal)),
            sql => Assert.Contains("[ProjectId] = @", sql));
    }

    [Fact]
    public async Task Without_see_money_the_expense_list_is_limited_to_the_callers_own_records_in_sql()
    {
        var (app, database) = await StartAsync(new VisibleProject(IsCompleted: false, Responsibility: ProjectResponsibilities.SiteEngineer, Executive: false));
        await using var _ = app;
        var token = TestTokens.Create(userId: 42, role: "engineer", permissions: [("projects", "VC")]);

        var response = await Send(app, "GET", "/api/projects/1/expenses", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("summary").ValueKind);
        var count = Assert.Single(database.Commands, sql => sql.TrimStart().StartsWith("SELECT COUNT(*)", StringComparison.Ordinal));
        Assert.Contains("[CreatedBy] = @", count);
    }

    [Fact]
    public async Task Supplier_search_text_is_sent_as_a_parameter_never_inside_the_sql()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "V")]);

        var response = await Send(app, "GET", "/api/suppliers?search=x'%20OR%201=1--&status=active&kind=fuel", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(database.Commands, sql => Assert.DoesNotContain("OR 1=1", sql));
    }

    [Fact]
    public async Task A_supplier_that_does_not_exist_answers_404_on_edit()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: Executive, permissions: [("projects", "VCEAF")]);

        var response = await Send(app, "PUT", "/api/suppliers/1", token, ValidBody("PUT", "/api/suppliers/1"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private const string RowVersion = "AAAAAAAAB9E=";

    /// <summary>A body that passes validation for each endpoint, so tests reach the checks after it.</summary>
    internal static object? ValidBody(string method, string path)
    {
        if (method == "GET" || method == "DELETE")
        {
            return null;
        }

        if (path.Contains("/supplier-payments", StringComparison.Ordinal))
        {
            return path.EndsWith("/void", StringComparison.Ordinal)
                ? new { reason = "Entered twice", rowVersion = RowVersion }
                : PaymentBody(1000, 1000);
        }

        if (path.StartsWith("/api/suppliers", StringComparison.Ordinal))
        {
            return new { nameAr = "شركة الأسمنت", nameEn = "Cement Co", kind = "Materials", status = "Active", rowVersion = RowVersion };
        }

        if (path.Contains("/items", StringComparison.Ordinal) && !path.Contains("/expenses", StringComparison.Ordinal))
        {
            return path.EndsWith("activate", StringComparison.Ordinal)
                ? new { rowVersion = RowVersion }
                : new { nameAr = "الأعمال الخرسانية", nameEn = "Concrete works", category = "Concrete", unit = "M3", rowVersion = RowVersion };
        }

        if (path.EndsWith("/adjustments", StringComparison.Ordinal))
        {
            return new { amount = -100m, reason = "Charged twice for the same truck" };
        }

        if (path.EndsWith("/item", StringComparison.Ordinal))
        {
            return new { itemId = 3, rowVersion = RowVersion };
        }

        if (path.EndsWith("/approve", StringComparison.Ordinal) || path.EndsWith("/return", StringComparison.Ordinal) || path.EndsWith("/reject", StringComparison.Ordinal))
        {
            return new { note = "Please attach the invoice", rowVersion = RowVersion };
        }

        return new
        {
            kind = "Expense",
            expenseDate = "2026-01-15",
            category = "Lab",
            description = "Concrete cube tests",
            amount = 450,
            channel = "Custody",
            rowVersion = RowVersion,
        };
    }

    private static object PaymentBody(decimal amount, decimal allocated) => new
    {
        supplierId = 1,
        paymentDate = "2026-01-20",
        amount,
        method = "BankTransfer",
        reference = "TRF-1",
        idempotencyKey = Guid.NewGuid().ToString(),
        allocations = new[] { new { expenseId = 2, amount = allocated } },
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
    private sealed record VisibleProject(bool IsCompleted, string? Responsibility, bool Executive) : IProjectAccess
    {
        public Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectAccessInfo?>(new ProjectAccessInfo(projectId, "P-1", "مشروع", "Project", IsCompleted, Responsibility, Executive));
    }
}
