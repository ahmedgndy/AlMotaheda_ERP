using System.Globalization;
using System.Net;
using System.Text.Json;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Subcontractors.Tests.Database;

/// <summary>
/// Workflow 10, across modules on SQL Server: supplier records (Projects), subcontract work (Subcontractors) and
/// labor (Workforce) on one project, through the real endpoints. The items table, an item's detail, the "not linked
/// yet" block and the overview must all tell the same story, nothing may be counted twice, and linking a loose record
/// must move money without adding a row. A second project must stay empty.
/// </summary>
/// <remarks>
/// Figures, all approved unless said otherwise:
/// <list type="bullet">
/// <item>Supplies: 100 × 1,000 = 100,000 on Concrete (credit, 60,000 paid); 20 × 500 = 10,000 not linked (unpaid);
/// 5 × 1,000 on Concrete still waiting for review (never cost).</item>
/// <item>Subcontract (value 500,000 = commitment, never cost): 30 m³ × 800 = 24,000 on Plumbing; 10 × 300 = 3,000 not
/// linked; one payment of 10,000 settles the oldest work (the Plumbing measurement).</item>
/// <item>Labor: plumber 1 a full day at 500 on Plumbing (200 paid); plumber 2 a day at 500, half on Plumbing (250) and
/// half not linked (250).</item>
/// </list>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ProjectReconciliationTests(SqlServerFixture database)
{
    private const int PlumberTrade = 10;

    [DatabaseFact]
    public async Task Suppliers_subcontracts_and_labor_reconcile_across_the_items_table_the_details_and_the_overview()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineerId, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var projectId = (await api.CreateProjectAsync(executive, "REC-A", managerId, siteEngineers: [engineerId])).GetProperty("id").GetInt32();
        var otherProjectId = (await api.CreateProjectAsync(executive, "REC-B", managerId)).GetProperty("id").GetInt32();
        var concrete = await CreateItemAsync(api, executive, projectId, "Concrete works", "Concrete");
        var plumbing = await CreateItemAsync(api, executive, projectId, "Plumbing and drainage", "Mep");

        // Supplier side (Projects): the engineer records, the accountant approves and pays.
        var supplier = await OkAsync(api, executive, HttpMethod.Post, "/api/suppliers", new
        {
            nameAr = "مورد خرسانة", nameEn = "Ready Mix Co", kind = "Materials", phone = "0100000000",
        }, HttpStatusCode.Created);
        var supplierId = supplier.Id();
        var linkedSupply = await ApprovedSupplyAsync(api, engineer, accountant, projectId, supplierId, 100m, 1_000m, concrete);
        var looseSupply = await ApprovedSupplyAsync(api, engineer, accountant, projectId, supplierId, 20m, 500m, itemId: null);
        await OkAsync(api, engineer, HttpMethod.Post, $"/api/projects/{projectId}/expenses",
            SupplyBody(supplierId, 5m, 1_000m, concrete), HttpStatusCode.Created); // waiting for review: never cost
        await OkAsync(api, accountant, HttpMethod.Post, $"/api/projects/{projectId}/supplier-payments", new
        {
            supplierId,
            paymentDate = "2026-01-12",
            amount = 60_000m,
            method = "BankTransfer",
            reference = "TRF-REC-1",
            idempotencyKey = Guid.NewGuid().ToString(),
            allocations = new[] { new { expenseId = linkedSupply.Id(), amount = 60_000m } },
        }, HttpStatusCode.Created);

        // Subcontract side: the contract value is a commitment; only approved measured work is cost.
        var company = await api.CreateCompanyAsync(executive, "Delta Plumbing", "Plumbing");
        var contractId = (await api.CreateContractAsync(manager, projectId, SubcontractsApi.ContractBody(company, "UnitRate", 500_000m))).ContractId();
        var plumbingLine = await api.AddItemAsync(manager, projectId, contractId, 100m, 800m, projectItemId: plumbing);
        var looseLine = await api.AddItemAsync(manager, projectId, contractId, 10m, 300m);
        await api.ApprovedAsync(executive, projectId, "measurements", await api.MeasuredAsync(manager, projectId, contractId, plumbingLine.Id(), 30m, "2026-03-01"));
        await api.ApprovedAsync(executive, projectId, "measurements", await api.MeasuredAsync(manager, projectId, contractId, looseLine.Id(), 10m, "2026-03-02"));
        await api.PaidAsync(accountant, projectId, contractId, 10_000m, "2026-03-10");

        // Labor side (Workforce): the site engineer records a day, the manager approves it, the accountant pays.
        var plumber1 = await HireAsync(api, executive, projectId, "Plumber One");
        var plumber2 = await HireAsync(api, executive, projectId, "Plumber Two");
        var workDate = Today(api).AddDays(-1);
        var sheet = await OkAsync(api, engineer, HttpMethod.Post, $"/api/projects/{projectId}/labor/sheets", new
        {
            workDate = Iso(workDate),
            shift = "Day",
            submit = true,
            lines = new object[]
            {
                LaborLine(plumber1, (plumbing, 1m)),
                LaborLine(plumber2, (plumbing, 0.5m)),
            },
        }, HttpStatusCode.Created);
        await OkAsync(api, manager, HttpMethod.Post, $"/api/projects/{projectId}/labor/sheets/{sheet.Id()}/approve",
            new { note = (string?)null, rowVersion = sheet.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);
        await OkAsync(api, accountant, HttpMethod.Post, $"/api/workers/{plumber1}/payments", new
        {
            paymentDate = Iso(Today(api)),
            amount = 200m,
            method = "Cash",
            reference = "Site cash",
            idempotencyKey = Guid.NewGuid().ToString(),
        }, HttpStatusCode.Created);

        // The items table: every row is its sources, the footer is the rows, "not linked" is separate.
        var items = await OkAsync(api, executive, HttpMethod.Get, $"/api/projects/{projectId}/items", null, HttpStatusCode.OK);
        var rows = items.GetProperty("items").EnumerateArray().ToDictionary(row => row.Id());
        AssertFigures(rows[concrete], supplier: 100_000m, subcontractor: 0m, labor: 0m, paid: 60_000m);
        AssertFigures(rows[plumbing], supplier: 0m, subcontractor: 24_000m, labor: 750m, paid: 10_200m);
        AssertFigures(items.GetProperty("totals"), supplier: 100_000m, subcontractor: 24_000m, labor: 750m, paid: 70_200m);
        AssertFigures(items.GetProperty("unallocated"), supplier: 10_000m, subcontractor: 3_000m, labor: 250m, paid: 0m);
        Assert.Equal(
            items.GetProperty("totals").Money("totalCost"),
            rows.Values.Sum(row => row.Money("totalCost")));

        var reconciliation = items.GetProperty("reconciliation");
        Assert.Equal(124_750m, reconciliation.Money("itemsTotalCost"));
        Assert.Equal(13_250m, reconciliation.Money("unallocatedTotalCost"));
        Assert.Equal(0m, reconciliation.Money("otherCosts"));
        Assert.Equal(138_000m, reconciliation.Money("projectRecognizedCost"));

        // An item's detail uses the same rules as its row, and names every record behind the number.
        var plumbingDetail = await OkAsync(api, executive, HttpMethod.Get, $"/api/projects/{projectId}/items/{plumbing}", null, HttpStatusCode.OK);
        AssertFigures(plumbingDetail.GetProperty("totals"), supplier: 0m, subcontractor: 24_000m, labor: 750m, paid: 10_200m);
        Assert.Equal(24_000m, plumbingDetail.GetProperty("subcontractLines").EnumerateArray().Sum(line => line.Money("amount")));
        Assert.Equal(
            new[] { plumber1, plumber2 }.Order(),
            plumbingDetail.GetProperty("laborLines").EnumerateArray().Select(line => line.GetProperty("workerId").GetInt32()).Order());
        Assert.Equal(750m, plumbingDetail.GetProperty("laborLines").EnumerateArray().Sum(line => line.Money("amount")));

        var concreteDetail = await OkAsync(api, executive, HttpMethod.Get, $"/api/projects/{projectId}/items/{concrete}", null, HttpStatusCode.OK);
        Assert.Equal([linkedSupply.Id()], concreteDetail.GetProperty("supplierLines").EnumerateArray().Select(line => line.GetProperty("expenseId").GetInt32()));
        Assert.Single(concreteDetail.GetProperty("pendingSupplierLines").EnumerateArray());

        var notLinked = await OkAsync(api, executive, HttpMethod.Get, $"/api/projects/{projectId}/items/unallocated", null, HttpStatusCode.OK);
        AssertFigures(notLinked.GetProperty("totals"), supplier: 10_000m, subcontractor: 3_000m, labor: 250m, paid: 0m);
        Assert.Single(notLinked.GetProperty("supplierLines").EnumerateArray());
        Assert.Single(notLinked.GetProperty("subcontractLines").EnumerateArray());
        Assert.Single(notLinked.GetProperty("laborLines").EnumerateArray());

        // The overview reads the same sources: recognized equals the items reconciliation, and the commitment
        // (contract value not yet executed) stays a separate figure.
        var overview = await OkAsync(api, executive, HttpMethod.Get, $"/api/projects/{projectId}/overview", null, HttpStatusCode.OK);
        var overviewTotals = overview.GetProperty("totals");
        Assert.Equal(138_000m, overviewTotals.Money("recognized"));
        Assert.Equal(70_200m, overviewTotals.Money("paid"));
        Assert.Equal(67_800m, overviewTotals.Money("outstanding"));
        Assert.Equal(473_000m, overviewTotals.Money("committed"));
        Assert.Equal(124_750m, overview.GetProperty("items").GetProperty("totals").Money("totalCost"));
        Assert.Equal(13_250m, overview.GetProperty("items").GetProperty("notLinked").Money("totalCost"));

        // Workflow 6: the loose supply and the loose contract line are linked to Concrete. The same records move;
        // nothing is added, and the project's recognized cost does not change.
        var expensesBefore = await CountExpensesAsync(api, executive, projectId);
        await OkAsync(api, manager, HttpMethod.Put, $"/api/projects/{projectId}/expenses/{looseSupply.Id()}/item",
            new { itemId = concrete, rowVersion = looseSupply.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);
        await EnsureStatus(await api.LinkItemAsync(manager, projectId, contractId, looseLine, concrete), HttpStatusCode.OK);

        var after = await OkAsync(api, executive, HttpMethod.Get, $"/api/projects/{projectId}/items", null, HttpStatusCode.OK);
        var afterRows = after.GetProperty("items").EnumerateArray().ToDictionary(row => row.Id());
        AssertFigures(afterRows[concrete], supplier: 110_000m, subcontractor: 3_000m, labor: 0m, paid: 60_000m);
        AssertFigures(after.GetProperty("unallocated"), supplier: 0m, subcontractor: 0m, labor: 250m, paid: 0m);
        Assert.Equal(138_000m, after.GetProperty("reconciliation").Money("projectRecognizedCost"));
        Assert.Equal(expensesBefore, await CountExpensesAsync(api, executive, projectId));

        // Linking to an item of another project is refused and changes nothing.
        var foreignItem = await CreateItemAsync(api, executive, otherProjectId, "Foreign item", "Concrete");
        var current = await OkAsync(api, executive, HttpMethod.Get, $"/api/projects/{projectId}/expenses/{looseSupply.Id()}", null, HttpStatusCode.OK);
        var crossProject = await api.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/expenses/{looseSupply.Id()}/item", manager,
            new { itemId = foreignItem, rowVersion = current.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);

        // The other project saw none of it.
        var other = await OkAsync(api, executive, HttpMethod.Get, $"/api/projects/{otherProjectId}/items", null, HttpStatusCode.OK);
        Assert.Equal(0m, other.GetProperty("totals").Money("totalCost"));
        Assert.Equal(0m, other.GetProperty("unallocated").Money("totalCost"));
        Assert.Equal(0m, other.GetProperty("reconciliation").Money("projectRecognizedCost"));
    }

    private static void AssertFigures(JsonElement figures, decimal supplier, decimal subcontractor, decimal labor, decimal paid)
    {
        Assert.Equal(supplier, figures.Money("supplierCost"));
        Assert.Equal(subcontractor, figures.Money("subcontractorCost"));
        Assert.Equal(supplier + subcontractor, figures.Money("supplierAndSubcontractorCost"));
        Assert.Equal(labor, figures.Money("laborCost"));
        Assert.Equal(supplier + subcontractor + labor, figures.Money("totalCost"));
        Assert.Equal(paid, figures.Money("paid"));
        Assert.Equal(supplier + subcontractor + labor - paid, figures.Money("outstanding"));
    }

    private static async Task<JsonElement> OkAsync(ProjectsApi api, Session session, HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        var response = await api.SendAsync(method, path, session, body);
        await EnsureStatus(response, expected);
        return await Json(response);
    }

    private static async Task<int> CreateItemAsync(ProjectsApi api, Session session, int projectId, string nameEn, string category) =>
        (await OkAsync(api, session, HttpMethod.Post, $"/api/projects/{projectId}/items",
            new { nameAr = $"بند {nameEn}", nameEn, category, unit = "Lot" }, HttpStatusCode.Created)).Id();

    private static Dictionary<string, object?> SupplyBody(int supplierId, decimal quantity, decimal unitPrice, int? itemId) => new()
    {
        ["kind"] = "Supply",
        ["expenseDate"] = "2026-01-10",
        ["supplierId"] = supplierId,
        ["itemName"] = "Ready mix C30",
        ["unit"] = "Bag",
        ["quantity"] = quantity,
        ["unitPrice"] = unitPrice,
        ["channel"] = "Credit",
        ["invoiceNo"] = $"INV-{Guid.NewGuid():N}"[..12],
        ["deliveryNoteNo"] = "DN-1",
        ["projectItemId"] = itemId,
    };

    private static async Task<JsonElement> ApprovedSupplyAsync(
        ProjectsApi api, Session recorder, Session reviewer, int projectId, int supplierId, decimal quantity, decimal unitPrice, int? itemId)
    {
        var recorded = await OkAsync(api, recorder, HttpMethod.Post, $"/api/projects/{projectId}/expenses",
            SupplyBody(supplierId, quantity, unitPrice, itemId), HttpStatusCode.Created);
        return await OkAsync(api, reviewer, HttpMethod.Post, $"/api/projects/{projectId}/expenses/{recorded.Id()}/approve",
            new { note = (string?)null, rowVersion = recorded.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);
    }

    private static async Task<int> HireAsync(ProjectsApi api, Session executive, int projectId, string nameEn)
    {
        var worker = await OkAsync(api, executive, HttpMethod.Post, "/api/workers",
            new { nameAr = $"عامل {nameEn}", nameEn, tradeId = PlumberTrade, dailyRate = 500m }, HttpStatusCode.Created);
        await OkAsync(api, executive, HttpMethod.Post, $"/api/projects/{projectId}/workers",
            new { workerId = worker.Id(), dailyRate = (decimal?)null, startDate = Iso(Today(api).AddDays(-10)) }, HttpStatusCode.Created);
        return worker.Id();
    }

    private static object LaborLine(int workerId, params (int ItemId, decimal Portion)[] allocations) => new
    {
        workerId,
        days = 1m,
        overtimeHours = 0m,
        adjustments = Array.Empty<object>(),
        allocations = allocations.Select(allocation => new { itemId = allocation.ItemId, portion = allocation.Portion }).ToArray(),
    };

    private static async Task<int> CountExpensesAsync(ProjectsApi api, Session session, int projectId) =>
        (await OkAsync(api, session, HttpMethod.Get, $"/api/projects/{projectId}/expenses?pageSize=100", null, HttpStatusCode.OK))
            .GetProperty("totalCount").GetInt32();

    private static DateOnly Today(ProjectsApi api) => api.Access.App.Services.GetRequiredService<CompanyClock>().Today;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

