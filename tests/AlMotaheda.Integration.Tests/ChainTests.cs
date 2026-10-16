using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Fleet.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using AlMotaheda.Modules.Workforce.Tests.Database;
using Microsoft.AspNetCore.SignalR.Client;

namespace AlMotaheda.Integration.Tests;

/// <summary>
/// The brief's Phase 8 chains, end to end on SQL Server: each one checks that the same number is identical on the
/// project screens (for the manager, the accountant and the Executive), in the reports and in the ledger, and that
/// the financial steps are in the audit trail. Every helper is called by its class name: the module helpers share
/// method names.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ChainTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Daily_team_to_ledger_to_project_labor_cost_and_cost_item()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await WorkforceApi.CastAsync(api, "INT-LAB-01");
        var item = await WorkforceApi.CreateItemAsync(api, cast.Executive, cast.ProjectId, "Plumbing");
        var worker = await WorkforceApi.HireAsync(api, cast.Executive, cast.ProjectId, WorkforceApi.Plumber, "Saad", dailyRate: 400m);

        // Daily team recorded by the engineer, approved by the manager, paid through Finance (request → Executive → paid).
        await WorkforceApi.RecordApprovedSheetAsync(api, cast, cast.ProjectId, WorkforceApi.Today(api).AddDays(-1), WorkforceApi.Line(worker, 1m, [(item, 1m)]));
        await WorkforceApi.PaidAsync(api, cast.Accountant, worker, 400m);

        // The project's labor cost: the same on the manager's, the accountant's and the Executive's overview.
        foreach (var session in new[] { cast.Manager, cast.Accountant, cast.Executive })
        {
            var labor = Group(await Get(api, session, $"/api/projects/{cast.ProjectId}/overview"), "Labor");
            Assert.Equal((400m, 400m), (labor.GetProperty("recognized").GetDecimal(), labor.GetProperty("paid").GetDecimal()));
        }

        // The reports say the same: cost summary, the item's labor column, the weekly payroll.
        var cost = Rows(await Get(api, cast.Accountant, $"/api/reports/PRJ-COST?projectId={cast.ProjectId}"));
        Assert.Equal(400m, cost.Single(row => row.GetProperty("costGroup").GetString() == "Labor").GetProperty("recognized").GetDecimal());
        var items = Rows(await Get(api, cast.Accountant, $"/api/reports/PRJ-ITEMS?projectId={cast.ProjectId}"));
        Assert.Equal((400m, 400m), (items.Single(row => row.GetProperty("item").GetString() == "Plumbing").GetProperty("laborCost").GetDecimal(),
            items.Single(row => row.GetProperty("item").GetString() == "Plumbing").GetProperty("paid").GetDecimal()));
        var today = WorkforceApi.Today(api);
        var payroll = await Get(api, cast.Accountant, $"/api/reports/WRK-PAYROLL?from={Iso(today.AddDays(-10))}&to={Iso(today)}");
        Assert.Equal((400m, 400m, 0m), (Total(payroll, "pay"), Total(payroll, "paid"), Total(payroll, "outstanding")));

        // The ledger: the money out is the paid labor, and the ledger report agrees with it.
        var ledger = await Get(api, cast.Accountant, "/api/ledger?counterpartyType=Worker");
        Assert.Equal(-400m, ledger.GetProperty("totals").GetProperty("net").GetDecimal());
        var ledgerReport = await Get(api, cast.Accountant, $"/api/reports/FIN-LEDGER?from={Iso(today.AddDays(-30))}&to={Iso(today.AddDays(1))}&counterpartyType=Worker");
        Assert.Equal(-400m, Total(ledgerReport, "amount"));

        // Every financial step is in the audit trail: who, when, and the old and new status.
        var trail = (await Get(api, cast.Executive, "/api/audit-entries?entityType=PaymentRequest&pageSize=100")).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(trail, entry => entry.GetProperty("action").GetString() == "Added" && entry.GetProperty("userId").ValueKind == JsonValueKind.Number);
        Assert.Contains(trail, entry => entry.GetProperty("changes").TryGetProperty("Status", out var status)
                                        && status[0].GetString() == "Submitted" && status[1].GetString() is "Approved" or "Paid");
    }

    [DatabaseFact]
    public async Task Fuel_and_maintenance_reach_the_project_cost_and_the_equipment_reports()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await FleetApi.CastAsync(api);
        var machine = await FleetApi.RegisterAsync(api, cast.Executive, FleetApi.EquipmentBody("INT-EX-1"));
        await FleetApi.AssignedAsync(api, cast.Manager, cast.ProjectA, FleetApi.Id(machine), "2026-01-05");

        var fuel = await FleetApi.RecordedCostAsync(api, cast.Manager, cast.ProjectA, FleetApi.FuelBody(FleetApi.Id(machine), 100m, 15m));
        var repair = await FleetApi.RecordedCostAsync(api, cast.Manager, cast.ProjectA, FleetApi.LumpBody(FleetApi.Id(machine), "Maintenance", 2_000m));
        await FleetApi.ReviewedAsync(api, cast.Accountant, cast.ProjectA, fuel, "approve");
        await FleetApi.ReviewedAsync(api, cast.Accountant, cast.ProjectA, repair, "approve");

        foreach (var session in new[] { cast.Manager, cast.Accountant, cast.Executive })
        {
            Assert.Equal(3_500m, Group(await Get(api, session, $"/api/projects/{cast.ProjectA}/overview"), "Equipment").GetProperty("recognized").GetDecimal());
        }

        var equipment = await Get(api, cast.Accountant, $"/api/reports/EQP-COST?projectId={cast.ProjectA}");
        Assert.Equal((1_500m, 2_000m, 3_500m), (Total(equipment, "fuel"), Total(equipment, "maintenance"), Total(equipment, "total")));
        var fuelReport = await Get(api, cast.Accountant, $"/api/reports/EQP-FUEL?projectId={cast.ProjectA}");
        Assert.Equal((100m, 1_500m), (Total(fuelReport, "liters"), Total(fuelReport, "cost")));
        Assert.Equal(3_500m, Rows(await Get(api, cast.Accountant, $"/api/reports/PRJ-COST?projectId={cast.ProjectA}"))
            .Single(row => row.GetProperty("costGroup").GetString() == "Equipment").GetProperty("recognized").GetDecimal());
    }

    [DatabaseFact]
    public async Task A_price_change_approved_by_the_executive_reaches_the_catalog_live_and_the_next_requisition()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await CoreApi.CastAsync(api, "INT-PRICE-01");
        var (_, purchasing) = await api.CreateSignedInUserAsync(cast.Executive, "purchasing");
        var catalog = $"/api/projects/{cast.ProjectId}/catalog-items";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var cement = (await Send(api, cast.Manager, HttpMethod.Post, catalog,
            new { nameAr = "أسمنت", nameEn = "Cement", category = "CementAggregates", unit = "Bag", price = 150m, effectiveFrom = Iso(today.AddDays(-5)) },
            HttpStatusCode.Created)).GetProperty("item").GetProperty("id").GetInt32();

        // Purchasing watches the company's procurement group.
        var signals = new ConcurrentBag<JsonElement>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(api.Access.App.Client.BaseAddress!, "/api/hubs/erp"), options => options.AccessTokenProvider = () => Task.FromResult<string?>(purchasing.AccessToken))
            .Build();
        hub.On<JsonElement>("changed", signal => signals.Add(signal));
        await hub.StartAsync();
        Assert.True(await hub.InvokeAsync<bool>("Join", "company:procurement"));

        var change = await Send(api, cast.Manager, HttpMethod.Post, $"{catalog}/{cement}/price-changes",
            new { price = 160m, effectiveFrom = Iso(today.AddDays(-1)), reason = "Supplier price list" }, HttpStatusCode.Created);
        await Send(api, cast.Executive, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/price-changes/{change.GetProperty("id").GetInt32()}/approve", new { });

        // The catalog shows the new price at once (the catalog isn't cached), Purchasing hears about it live...
        Assert.Equal(160m, (await Get(api, cast.Manager, $"{catalog}/{cement}")).GetProperty("item").GetProperty("currentPrice").GetDecimal());
        await WaitAsync(() => signals.Any(signal => signal.GetProperty("entityType").GetString() == "ItemPrice"));

        // ...and the next requisition is estimated at it.
        var requisition = await Send(api, cast.Executive, HttpMethod.Post, "/api/procurement/requisitions",
            new { projectId = cast.ProjectId, lines = new[] { new { catalogItemId = cement, quantity = 10m } } }, HttpStatusCode.Created);
        Assert.Equal(160m, requisition.GetProperty("lines")[0].GetProperty("estimatedUnitPrice").GetDecimal());
    }

    [DatabaseFact]
    public async Task Supplier_invoice_to_payment_to_supplier_balance_cost_item_and_project_total()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await CoreApi.CastAsync(api, "INT-SUP-01");
        var (_, purchasing) = await api.CreateSignedInUserAsync(cast.Executive, "purchasing");
        var supplier = await CoreApi.CreateSupplierAsync(api, purchasing, "Ezz Steel");
        var costItem = CoreApi.Id(await CoreApi.CreateItemAsync(api, cast.Manager, cast.ProjectId, "Foundations"));
        var today = Iso(DateOnly.FromDateTime(DateTime.UtcNow));

        var requisition = await Send(api, cast.Engineer, HttpMethod.Post, "/api/procurement/requisitions",
            new { projectId = cast.ProjectId, lines = new[] { new { description = "Rebar 12 mm", unit = "Ton", projectItemId = costItem, quantity = 2m } } }, HttpStatusCode.Created);
        var order = await Send(api, purchasing, HttpMethod.Post, "/api/procurement/purchase-orders", new
        {
            requisitionId = requisition.GetProperty("id").GetInt32(), supplierId = supplier,
            lines = new[] { new { requisitionLineId = requisition.GetProperty("lines")[0].GetProperty("id").GetInt32(), quantity = 2m, unitPrice = 38_000m } },
        }, HttpStatusCode.Created);
        var orderLine = order.GetProperty("lines")[0].GetProperty("id").GetInt32();
        await Send(api, cast.Engineer, HttpMethod.Post, $"/api/procurement/purchase-orders/{order.GetProperty("id").GetInt32()}/receipts",
            new { receivedOn = today, lines = new[] { new { orderLineId = orderLine, quantity = 2m } } }, HttpStatusCode.Created);
        var invoice = await Send(api, cast.Accountant, HttpMethod.Post, $"/api/procurement/purchase-orders/{order.GetProperty("id").GetInt32()}/invoices",
            new { invoiceNo = "EZ-77", invoiceDate = today, lines = new[] { new { orderLineId = orderLine, quantity = 2m, unitPrice = 38_000m } } }, HttpStatusCode.Created);
        var record = invoice.GetProperty("lines")[0].GetProperty("postedRecordId").GetInt32();

        // Pay half through the supplier payment request → Executive → paid.
        var request = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/supplier-payments", cast.Accountant, new
        {
            supplierId = supplier, paymentDate = today, amount = 38_000m, method = "Cash", reference = "R-1", idempotencyKey = Guid.NewGuid().ToString(),
            allocations = new[] { new { expenseId = record, amount = 38_000m } },
        });
        await AccessApi.EnsureStatus(request, HttpStatusCode.Created);
        await api.SettleAsync(cast.Accountant, await AccessApi.Json(request));

        // The supplier balance, the cost item, the project total, the reports and the ledger all agree.
        var balances = await CoreApi.BalancesAsync(api, cast.Accountant, cast.ProjectId);
        Assert.Equal((38_000m, 38_000m), (balances.GetProperty("paid").GetDecimal(), balances.GetProperty("outstanding").GetDecimal()));
        var account = await Get(api, cast.Accountant, $"/api/reports/SUP-ACCOUNT?projectId={cast.ProjectId}&supplierId={supplier}");
        Assert.Equal((38_000m, 38_000m), (Total(account, "paid"), Total(account, "outstanding")));
        var items = Rows(await Get(api, cast.Accountant, $"/api/reports/PRJ-ITEMS?projectId={cast.ProjectId}"));
        Assert.Equal(76_000m, items.Single(row => row.GetProperty("item").GetString() == "Foundations").GetProperty("supplierCost").GetDecimal());
        foreach (var session in new[] { cast.Manager, cast.Accountant, cast.Executive })
        {
            var materials = Group(await Get(api, session, $"/api/projects/{cast.ProjectId}/overview"), "Materials");
            Assert.Equal((76_000m, 38_000m, 0m), (materials.GetProperty("recognized").GetDecimal(), materials.GetProperty("paid").GetDecimal(), materials.GetProperty("committed").GetDecimal()));
        }

        var ledger = await Get(api, cast.Accountant, $"/api/ledger?projectId={cast.ProjectId}");
        Assert.Equal(-38_000m, ledger.GetProperty("totals").GetProperty("net").GetDecimal());
        Assert.Equal("PartlyPaid", (await Get(api, cast.Accountant, $"/api/procurement/supplier-invoices/{invoice.GetProperty("id").GetInt32()}")).GetProperty("paymentState").GetString());
    }

    [DatabaseFact]
    public async Task Salary_sheet_to_accountant_to_payment_to_ledger_to_reports()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (_, hr) = await api.CreateSignedInUserAsync(executive, "hr");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var month = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        var monthText = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        var department = (await Send(api, hr, HttpMethod.Post, "/api/hr/departments", new { nameAr = "الإدارة", nameEn = "Administration" }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var employee = (await Send(api, hr, HttpMethod.Post, "/api/hr/employees", new
        {
            nameAr = "منى", nameEn = "Mona", nationalId = "29001011234567", jobTitle = "Secretary", departmentId = department, kind = "OfficeEmployee", hireDate = "2024-01-01",
        }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
        await Send(api, hr, HttpMethod.Post, $"/api/hr/employees/{employee}/salaries", new { effectiveFrom = "2024-01-01", basicSalary = 9_000m, fixedAllowance = 1_000m }, HttpStatusCode.Created);

        // HR prepares and submits; the Accountant approves and asks Finance to pay; the Executive pays.
        var sheet = await Send(api, hr, HttpMethod.Post, "/api/hr/salary-sheets", new { month = monthText }, HttpStatusCode.Created);
        sheet = await Send(api, hr, HttpMethod.Post, $"/api/hr/salary-sheets/{sheet.GetProperty("id").GetInt32()}/submit", new { rowVersion = sheet.GetProperty("rowVersion").GetString() });
        sheet = await Send(api, accountant, HttpMethod.Post, $"/api/hr/salary-sheets/{sheet.GetProperty("id").GetInt32()}/approve", new { rowVersion = sheet.GetProperty("rowVersion").GetString() });
        var request = await Send(api, accountant, HttpMethod.Post, $"/api/hr/salary-sheets/{sheet.GetProperty("id").GetInt32()}/payment-request",
            new { requestedDate = Iso(DateOnly.FromDateTime(DateTime.UtcNow)), method = "Cash", rowVersion = sheet.GetProperty("rowVersion").GetString() }, HttpStatusCode.Created);
        await api.SettleAsync(accountant, request);

        // The sheet, the salary report, the ledger, the cash report and the reconciliation all say 10,000.
        Assert.Equal("Paid", (await Get(api, accountant, $"/api/hr/salary-sheets/{sheet.GetProperty("id").GetInt32()}")).GetProperty("status").GetString());
        var salaries = await Get(api, accountant, $"/api/reports/HR-SALARIES?fromMonth={monthText}&toMonth={monthText}");
        Assert.Equal(10_000m, Total(salaries, "net"));
        Assert.Equal(-10_000m, (await Get(api, accountant, "/api/ledger?counterpartyType=Employees")).GetProperty("totals").GetProperty("net").GetDecimal());
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var window = $"from={Iso(today.AddDays(-2))}&to={Iso(today.AddDays(2))}";
        Assert.Equal(10_000m, Total(await Get(api, accountant, $"/api/reports/FIN-CASH?{window}&period=Month"), "moneyOut"));
        var recon = await Get(api, accountant, $"/api/reports/FIN-RECON?{window}");
        Assert.Equal(0m, Total(recon, "difference"));

        // HR sees its salaries report but never the ledger.
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, $"/api/reports/HR-SALARIES?fromMonth={monthText}&toMonth={monthText}", hr)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/ledger", hr)).StatusCode);
    }

    private static JsonElement Group(JsonElement overview, string costGroup) =>
        overview.GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("costGroup").GetString() == costGroup);

    private static List<JsonElement> Rows(JsonElement report) =>
        report.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("cells")).ToList();

    private static decimal Total(JsonElement report, string column) => report.GetProperty("totals").GetProperty(column).GetDecimal();

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Task<JsonElement> Get(ProjectsApi api, Session session, string path) => Send(api, session, HttpMethod.Get, path, null);

    private static async Task<JsonElement> Send(ProjectsApi api, Session session, HttpMethod method, string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await api.SendAsync(method, path, session, body);
        await AccessApi.EnsureStatus(response, expected);
        return await AccessApi.Json(response);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 60 && !condition(); attempt++)
        {
            await Task.Delay(500);
        }

        Assert.True(condition(), "Waited 30 seconds for a live signal.");
    }
}
