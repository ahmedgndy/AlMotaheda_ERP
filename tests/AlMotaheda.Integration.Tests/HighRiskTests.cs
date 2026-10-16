using System.Globalization;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;

namespace AlMotaheda.Integration.Tests;

/// <summary>
/// The brief's Phase 9 high-risk list, on SQL Server with the real seeded roles: payroll arithmetic, no payment before
/// approval, a changed amount needs a new approval, nothing moves once paid, a retried form pays once, a stale form
/// is refused, and the role boundaries (Engineer, Accountant, HR). The other items of the list are proved in the
/// module tests (see docs/modernization/20-high-risk-tests.md).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class HighRiskTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_salary_sheet_is_paid_once_only_after_approval_and_a_changed_amount_needs_a_new_approval()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (_, hr) = await api.CreateSignedInUserAsync(executive, "hr");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var month = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var employee = await HireAsync(api, hr, basic: 9_000m, allowance: 1_000m);

        // Payroll: basic + allowance + bonus − deduction = 9,000 + 1,000 + 500 − 300.
        await AddInputAsync(api, hr, employee, month, "Bonus", 500m, HttpStatusCode.Created);
        await AddInputAsync(api, hr, employee, month, "Deduction", 300m, HttpStatusCode.Created);
        var sheet = await Send(api, hr, HttpMethod.Post, "/api/hr/salary-sheets", new { month }, HttpStatusCode.Created);
        var line = sheet.GetProperty("lines")[0];
        Assert.Equal((9_000m, 1_000m, 500m, 300m, 10_200m), (Money(line, "basicSalary"), Money(line, "fixedAllowance"), Money(line, "bonuses"),
            Money(line, "deductions"), Money(line, "netSalary")));
        Assert.Equal(10_200m, Money(sheet, "total"));
        var sheetPath = $"/api/hr/salary-sheets/{Id(sheet)}";
        var draftVersion = Version(sheet);
        sheet = await Send(api, hr, HttpMethod.Post, $"{sheetPath}/submit", new { rowVersion = draftVersion });

        // Not approved yet: it can't be paid. A stale form (the Draft's row version) can't approve it.
        await Fails(api, accountant, HttpMethod.Post, $"{sheetPath}/payment-request", PaymentBody(sheet), HttpStatusCode.Conflict, "invalid_status");
        await Fails(api, accountant, HttpMethod.Post, $"{sheetPath}/approve", new { rowVersion = draftVersion }, HttpStatusCode.Conflict, "concurrency_conflict");

        // While submitted the month's amounts are locked. Returned, the amount changes, and the review is cleared:
        // the new total needs a new approval.
        await AddInputAsync(api, hr, employee, month, "Bonus", 800m, HttpStatusCode.Conflict, "month_locked");
        sheet = await Send(api, accountant, HttpMethod.Post, $"{sheetPath}/return", new { reason = "Add the overtime bonus", rowVersion = Version(sheet) });
        await AddInputAsync(api, hr, employee, month, "Bonus", 800m, HttpStatusCode.Created);
        sheet = await Send(api, hr, HttpMethod.Post, $"{sheetPath}/submit", new { rowVersion = Version(sheet) });
        Assert.Equal(("Submitted", 11_000m), (Status(sheet), Money(sheet, "total")));
        await Fails(api, accountant, HttpMethod.Post, $"{sheetPath}/payment-request", PaymentBody(sheet), HttpStatusCode.Conflict, "invalid_status");
        sheet = await Send(api, accountant, HttpMethod.Post, $"{sheetPath}/approve", new { rowVersion = Version(sheet) });

        // Approved: amounts are frozen.
        await AddInputAsync(api, hr, employee, month, "Deduction", 100m, HttpStatusCode.Conflict, "month_locked");
        await Fails(api, hr, HttpMethod.Post, $"{sheetPath}/recalculate", new { rowVersion = Version(sheet) }, HttpStatusCode.Conflict, "invalid_status");

        // The same form sent twice is one request; HR can't pay it.
        var request = await Send(api, accountant, HttpMethod.Post, $"{sheetPath}/payment-request", PaymentBody(sheet), HttpStatusCode.Created);
        var retried = await Send(api, accountant, HttpMethod.Post, $"{sheetPath}/payment-request", PaymentBody(sheet));
        Assert.Equal(Id(request), Id(retried));
        var requestPath = $"/api/payment-requests/{Id(request)}";
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"{requestPath}/approve-and-pay", hr,
            new { paidOn = request.GetProperty("requestedDate").GetString(), rowVersion = "AAAAAAAAAAA=" })).StatusCode);

        // Paid once: paying again or with the old form is refused, the sheet can't move, and the ledger holds one payment.
        var unpaidVersion = Version(await Get(api, executive, requestPath));
        await api.SettleAsync(accountant, request);
        await Fails(api, executive, HttpMethod.Post, $"{requestPath}/approve-and-pay",
            new { paidOn = request.GetProperty("requestedDate").GetString(), rowVersion = unpaidVersion }, HttpStatusCode.Conflict);
        sheet = await Get(api, accountant, sheetPath);
        Assert.Equal("Paid", Status(sheet));
        await Fails(api, accountant, HttpMethod.Post, $"{sheetPath}/return", new { reason = "Too late", rowVersion = Version(sheet) }, HttpStatusCode.Conflict, "invalid_status");
        await Fails(api, hr, HttpMethod.Post, $"{sheetPath}/submit", new { rowVersion = Version(sheet) }, HttpStatusCode.Conflict, "invalid_status");
        await Fails(api, accountant, HttpMethod.Post, $"{sheetPath}/payment-request", PaymentBody(sheet), HttpStatusCode.Conflict, "invalid_status");
        await AddInputAsync(api, hr, employee, month, "Bonus", 1m, HttpStatusCode.Conflict, "month_locked");

        var ledger = await Get(api, accountant, "/api/ledger?counterpartyType=Employees");
        Assert.Equal(-11_000m, ledger.GetProperty("totals").GetProperty("net").GetDecimal());
        Assert.Single(ledger.GetProperty("items").EnumerateArray());
    }

    [DatabaseFact]
    public async Task The_real_roles_keep_to_their_boundaries()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (managerId, _) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineerId, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var (_, hr) = await api.CreateSignedInUserAsync(executive, "hr");
        var own = Id(await api.CreateProjectAsync(executive, "RISK-OWN", managerId, siteEngineers: [engineerId]));
        var other = Id(await api.CreateProjectAsync(executive, "RISK-OTHER", managerId));

        // The Engineer works on their own project and never sees another one: not in the list, 404 on every screen.
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{own}", engineer)).StatusCode);
        var listed = (await Get(api, engineer, "/api/projects")).GetProperty("items").EnumerateArray().Select(project => Id(project)).ToList();
        Assert.Equal([own], listed);
        foreach (var path in new[] { "", "/overview", "/expenses", "/items", "/members", "/daily-reports", "/supplier-balances", "/reports/cost-summary" })
        {
            // 404 where the Engineer may look on their own project; 403 where their role never may (money screens).
            var onOwn = (await api.SendAsync(HttpMethod.Get, $"/api/projects/{own}{path}", engineer)).StatusCode;
            var expected = onOwn == HttpStatusCode.OK ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden;
            Assert.True(expected == (await api.SendAsync(HttpMethod.Get, $"/api/projects/{other}{path}", engineer)).StatusCode, $"GET {path} on another project");
        }

        Assert.Contains((await api.SendAsync(HttpMethod.Get, $"/api/reports/PRJ-COST?projectId={other}", engineer)).StatusCode,
            new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        // The Accountant approves salary sheets but never adds or changes an employee.
        var employee = await HireAsync(api, hr, basic: 5_000m, allowance: 0m);
        var current = await Get(api, hr, $"/api/hr/employees/{employee}");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, "/api/hr/employees", accountant, EmployeeBody("29002021234567"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Put, $"/api/hr/employees/{employee}", accountant,
            EmployeeBody("29001011234567", Version(current)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/hr/employees/{employee}/salaries", accountant,
            new { effectiveFrom = "2025-01-01", basicSalary = 99_000m, fixedAllowance = 0m })).StatusCode);

        // HR never sees or moves money: no payment requests, no ledger, no project money.
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/payment-requests"),
                     (HttpMethod.Post, "/api/payment-requests/1/approve"),
                     (HttpMethod.Post, "/api/payment-requests/1/approve-and-pay"),
                     (HttpMethod.Post, "/api/payment-requests/1/confirm-payment"),
                     (HttpMethod.Get, "/api/ledger"),
                     (HttpMethod.Get, $"/api/reports/FIN-LEDGER?from=2026-01-01&to=2026-12-31"),
                 })
        {
            var response = await api.SendAsync(method, path, hr, method == HttpMethod.Get ? null : new { rowVersion = "AAAAAAAAAAA=", paidOn = "2026-01-01" });
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} answered {response.StatusCode} for HR.");
        }

        Assert.Contains((await api.SendAsync(HttpMethod.Get, $"/api/projects/{own}/overview", hr)).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
    }

    private static async Task<int> HireAsync(ProjectsApi api, Session hr, decimal basic, decimal allowance)
    {
        var department = Id(await Send(api, hr, HttpMethod.Post, "/api/hr/departments", new { nameAr = "الإدارة", nameEn = "Administration" }, HttpStatusCode.Created));
        var employee = Id(await Send(api, hr, HttpMethod.Post, "/api/hr/employees", EmployeeBody("29001011234567", departmentId: department), HttpStatusCode.Created));
        await Send(api, hr, HttpMethod.Post, $"/api/hr/employees/{employee}/salaries", new { effectiveFrom = "2024-01-01", basicSalary = basic, fixedAllowance = allowance }, HttpStatusCode.Created);
        return employee;
    }

    private static object EmployeeBody(string nationalId, string? rowVersion = null, int departmentId = 1) => new
    {
        nameAr = "منى", nameEn = "Mona", nationalId, jobTitle = "Secretary", departmentId, kind = "OfficeEmployee", hireDate = "2024-01-01", rowVersion,
    };

    private static async Task AddInputAsync(ProjectsApi api, Session hr, int employee, string month, string kind, decimal amount, HttpStatusCode expected, string? code = null)
    {
        var response = await api.SendAsync(HttpMethod.Post, "/api/hr/payroll-inputs", hr, new { employeeId = employee, month, kind, amount, reason = "Test" });
        await AccessApi.EnsureStatus(response, expected);
        if (code is not null)
        {
            Assert.Equal(code, await AccessApi.CodeOf(response));
        }
    }

    private static object PaymentBody(JsonElement sheet) => new
    {
        requestedDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        method = "Cash",
        rowVersion = Version(sheet),
    };

    private static Task<JsonElement> Get(ProjectsApi api, Session session, string path) => Send(api, session, HttpMethod.Get, path, null);

    private static async Task<JsonElement> Send(ProjectsApi api, Session session, HttpMethod method, string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await api.SendAsync(method, path, session, body);
        await AccessApi.EnsureStatus(response, expected);
        return await AccessApi.Json(response);
    }

    private static async Task Fails(ProjectsApi api, Session session, HttpMethod method, string path, object body, HttpStatusCode expected, string? code = null)
    {
        var response = await api.SendAsync(method, path, session, body);
        await AccessApi.EnsureStatus(response, expected);
        if (code is not null)
        {
            Assert.Equal(code, await AccessApi.CodeOf(response));
        }
    }

    private static int Id(JsonElement element) => element.GetProperty("id").GetInt32();

    private static string Version(JsonElement element) => element.GetProperty("rowVersion").GetString()!;

    private static string Status(JsonElement element) => element.GetProperty("status").GetString()!;

    private static decimal Money(JsonElement element, string name) => element.GetProperty(name).GetDecimal();
}
