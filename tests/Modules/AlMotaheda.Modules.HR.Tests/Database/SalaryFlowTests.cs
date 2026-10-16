using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.HR.Tests.Database;

/// <summary>
/// The HR flow on SQL Server: HR records employees and salaries and prepares the month; the Accountant returns it,
/// then approves it and asks Finance to pay it; the Executive approves and pays; the sheet is Paid and the ledger
/// shows the money out as company overhead.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SalaryFlowTests(SqlServerFixture database)
{
    /// <summary>Last month: always prepared-able, whatever day the tests run.</summary>
    private static readonly DateOnly Month = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);

    private static string MonthText => Month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    [DatabaseFact]
    public async Task A_month_goes_from_hr_to_the_accountant_to_finance_and_is_paid_once()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (_, hr) = await api.CreateSignedInUserAsync(executive, "hr");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var (_, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");

        var department = (await Created(api, hr, "/api/hr/departments", new { nameAr = "الحسابات", nameEn = "Accounts" })).Id();
        var mona = await Created(api, hr, "/api/hr/employees", Employee("Mona", "29001011234567", department, iban: "EG380019000500000000263180002"));
        var omar = await Created(api, hr, "/api/hr/employees", Employee("Omar", "29102021234567", department));
        Assert.Equal($"EMP-{mona.Id():000000}", mona.GetProperty("employeeNo").GetString());
        Assert.Equal("EG380019000500000000263180002", mona.GetProperty("iban").GetString());

        // One employee per national id.
        var twin = await api.SendAsync(HttpMethod.Post, "/api/hr/employees", hr, Employee("Twin", "29001011234567", department));
        Assert.Equal("national_id_exists", await CodeOf(twin));

        // The engineer has no HR access at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/hr/employees", engineer)).StatusCode);

        // Salaries from before the month, and a raise that starts after it.
        await Created(api, hr, $"/api/hr/employees/{mona.Id()}/salaries", new { effectiveFrom = "2025-01-01", basicSalary = 10_000m, fixedAllowance = 1_000m });
        await Created(api, hr, $"/api/hr/employees/{mona.Id()}/salaries", new { effectiveFrom = Month.AddMonths(1).ToString("yyyy-MM-dd"), basicSalary = 12_000m });
        await Created(api, hr, $"/api/hr/employees/{omar.Id()}/salaries", new { effectiveFrom = "2025-01-01", basicSalary = 5_000m });

        await Created(api, hr, "/api/hr/payroll-inputs", new { employeeId = mona.Id(), month = MonthText, kind = "Bonus", amount = 500m, reason = "Year-end closing" });
        var deduction = await Created(api, hr, "/api/hr/payroll-inputs", new { employeeId = omar.Id(), month = MonthText, kind = "Deduction", amount = 200m, reason = "Late days" });

        // HR prepares: 11,000 + 500 for Mona, 5,000 − 200 for Omar.
        var sheet = await Created(api, hr, "/api/hr/salary-sheets", new { month = MonthText });
        Assert.Equal(("Draft", 16_300m, 2), (sheet.GetProperty("status").GetString(), sheet.GetProperty("total").GetDecimal(), sheet.GetProperty("employeeCount").GetInt32()));
        Assert.Equal("sheet_exists", await CodeOf(await api.SendAsync(HttpMethod.Post, "/api/hr/salary-sheets", hr, new { month = MonthText })));

        sheet = await Step(api, hr, sheet, "submit");
        Assert.Equal("Submitted", sheet.GetProperty("status").GetString());

        // Submitted: HR can't approve its own sheet (no hr Approve), and the month's amounts are frozen.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/hr/salary-sheets/{sheet.Id()}/approve", hr,
            new { rowVersion = sheet.RowVersion() })).StatusCode);
        var locked = await api.SendAsync(HttpMethod.Post, "/api/hr/payroll-inputs", hr,
            new { employeeId = omar.Id(), month = MonthText, kind = "Bonus", amount = 1m, reason = "Too late" });
        Assert.Equal("month_locked", await CodeOf(locked));

        // The Accountant returns it with a reason; HR cancels the deduction and submits again.
        sheet = await Step(api, accountant, sheet, "return", new { reason = "Omar's late days were excused" });
        Assert.Equal(("Returned", "Omar's late days were excused"), (sheet.GetProperty("status").GetString(), sheet.GetProperty("reviewNote").GetString()));
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/hr/payroll-inputs/{deduction.Id()}/cancel", hr,
            new { reason = "Excused", rowVersion = deduction.RowVersion() }), HttpStatusCode.OK);
        sheet = await Step(api, hr, sheet, "submit");
        Assert.Equal(16_500m, sheet.GetProperty("total").GetDecimal());

        sheet = await Step(api, accountant, sheet, "approve", new { note = "Checked" });
        Assert.Equal("Approved", sheet.GetProperty("status").GetString());

        // HR can't ask for money; the Accountant can, once.
        var paymentBody = new { requestedDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), method = "Cash", rowVersion = sheet.RowVersion() };
        Assert.Equal(HttpStatusCode.Forbidden,
            (await api.SendAsync(HttpMethod.Post, $"/api/hr/salary-sheets/{sheet.Id()}/payment-request", hr, paymentBody)).StatusCode);
        var request = await Created(api, accountant, $"/api/hr/salary-sheets/{sheet.Id()}/payment-request", paymentBody);
        Assert.Equal(("Submitted", 16_500m, "Salaries", "Employees"), (request.GetProperty("status").GetString(),
            request.GetProperty("amount").GetDecimal(), request.GetProperty("purpose").GetString(), request.GetProperty("payeeType").GetString()));
        Assert.Equal(JsonValueKind.Null, request.GetProperty("projectId").ValueKind);

        // The same form again returns the same request; it can't be requested twice.
        var again = await api.SendAsync(HttpMethod.Post, $"/api/hr/salary-sheets/{sheet.Id()}/payment-request", accountant, paymentBody);
        Assert.Equal((HttpStatusCode.OK, request.Id()), (again.StatusCode, (await Json(again)).Id()));

        // The Executive approves and pays (cash): the sheet is Paid in the same transaction.
        var detail = await Json(await api.SendAsync(HttpMethod.Get, $"/api/payment-requests/{request.Id()}", executive));
        Assert.Equal($"Salaries {MonthText}", detail.GetProperty("request").GetProperty("payeeNameEn").GetString());
        var paid = await api.SendAsync(HttpMethod.Post, $"/api/payment-requests/{request.Id()}/approve-and-pay", executive,
            new { paidOn = DateTime.UtcNow.ToString("yyyy-MM-dd"), rowVersion = detail.GetProperty("rowVersion").GetString() });
        await EnsureStatus(paid, HttpStatusCode.OK);
        Assert.Equal("Paid", (await Json(paid)).GetProperty("request").GetProperty("status").GetString());

        var paidSheet = await Json(await api.SendAsync(HttpMethod.Get, $"/api/hr/salary-sheets/{sheet.Id()}", accountant));
        Assert.Equal(("Paid", request.Id()), (paidSheet.GetProperty("status").GetString(), paidSheet.GetProperty("paymentRequestId").GetInt32()));

        var ledger = await Json(await api.SendAsync(HttpMethod.Get, "/api/ledger?counterpartyType=Employees", accountant));
        var entry = Assert.Single(ledger.GetProperty("items").EnumerateArray());
        Assert.Equal((-16_500m, "Cash", "Salaries"), (entry.GetProperty("amount").GetDecimal(), entry.GetProperty("account").GetString(), entry.GetProperty("purpose").GetString()));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("projectId").ValueKind);
    }

    [DatabaseFact]
    public async Task The_sheet_names_employees_without_a_salary_and_nobody_reviews_their_own_sheet()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();

        var department = (await Created(api, executive, "/api/hr/departments", new { nameAr = "الموقع", nameEn = "Site" })).Id();
        var ali = await Created(api, executive, "/api/hr/employees", Employee("Ali", "28512121234567", department));

        var missing = await api.SendAsync(HttpMethod.Post, "/api/hr/salary-sheets", executive, new { month = MonthText });
        Assert.Equal("missing_salary", await CodeOf(missing));
        Assert.Contains(ali.GetProperty("employeeNo").GetString()!, (await Json(missing)).GetProperty("title").GetString());

        await Created(api, executive, $"/api/hr/employees/{ali.Id()}/salaries", new { effectiveFrom = "2025-01-01", basicSalary = 7_000m });
        var sheet = await Created(api, executive, "/api/hr/salary-sheets", new { month = MonthText });
        sheet = await Step(api, executive, sheet, "submit");

        // The Executive holds every HR permission, but still can't approve a sheet they submitted.
        var own = await api.SendAsync(HttpMethod.Post, $"/api/hr/salary-sheets/{sheet.Id()}/approve", executive, new { rowVersion = sheet.RowVersion() });
        Assert.Equal("own_record", await CodeOf(own));

        // An ended employee keeps their history but can't be edited.
        var ended = await api.SendAsync(HttpMethod.Post, $"/api/hr/employees/{ali.Id()}/end", executive,
            new { endDate = Month.ToString("yyyy-MM-dd"), reason = "Resigned", rowVersion = ali.RowVersion() });
        await EnsureStatus(ended, HttpStatusCode.OK);
        var edit = await api.SendAsync(HttpMethod.Put, $"/api/hr/employees/{ali.Id()}", executive,
            Employee("Ali", "28512121234567", department, rowVersion: (await Json(ended)).RowVersion()));
        Assert.Equal("employee_ended", await CodeOf(edit));
    }

    private static object Employee(string name, string nationalId, int departmentId, string? iban = null, string? rowVersion = null) => new
    {
        nameAr = $"{name} بالعربية",
        nameEn = name,
        nationalId,
        jobTitle = "Accountant",
        departmentId,
        kind = "OfficeEmployee",
        hireDate = "2024-06-01",
        iban,
        rowVersion,
    };

    private static async Task<JsonElement> Created(AccessApi api, Session session, string path, object body)
    {
        var response = await api.SendAsync(HttpMethod.Post, path, session, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    private static async Task<JsonElement> Step(AccessApi api, Session session, JsonElement sheet, string step, object? extra = null)
    {
        var body = JsonSerializer.SerializeToElement(extra ?? new { }).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);
        body["rowVersion"] = sheet.RowVersion();
        var response = await api.SendAsync(HttpMethod.Post, $"/api/hr/salary-sheets/{sheet.Id()}/{step}", session, body);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }
}

internal static class HrJson
{
    public static int Id(this JsonElement element) => element.GetProperty("id").GetInt32();

    public static string RowVersion(this JsonElement element) => element.GetProperty("rowVersion").GetString()!;
}
