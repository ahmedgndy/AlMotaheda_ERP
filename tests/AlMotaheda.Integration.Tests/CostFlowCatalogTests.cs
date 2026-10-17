using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Fleet.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using AlMotaheda.Modules.Workforce.Tests.Database;
using static AlMotaheda.Integration.Tests.CostFlowCalls;

namespace AlMotaheda.Integration.Tests;

/// <summary>
/// The master catalog, controlled prices, price variances and composite items (docs/cost-flow §2.2–2.4), end to end
/// on SQL Server with the real seeded roles: tests A, C, D and E of the brief.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CostFlowCatalogTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_a_project_uses_only_the_master_items_it_selects_and_the_catalog_is_never_copied()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api);
        var ex = people.Executive;

        var concrete = Id(await MasterItemAsync(api, ex, "Concrete work", "Composite", "Concrete", "M3"));
        var plumbing = Id(await MasterItemAsync(api, ex, "Plumbing", "Work", "Mep", "Lot"));
        var electrical = Id(await MasterItemAsync(api, ex, "Electrical work", "Work", "Mep", "Lot"));
        var cement = Id(await MasterItemAsync(api, ex, "Cement", "Material", "CementAggregates", "Bag", price: 20m));
        var steel = Id(await MasterItemAsync(api, ex, "Reinforcing steel", "Material", "Steel", "Ton", price: 40_000m));
        var transport = Id(await MasterItemAsync(api, ex, "Transport of materials", "Service", "Services", "Trip", price: 500m));
        var sand = Id(await MasterItemAsync(api, ex, "Sand", "Material", "CementAggregates", "M3", price: 150m));
        Assert.Equal(7, (await Get(api, people.Accountant, "/api/catalog/items")).GetProperty("totalCount").GetInt32());

        // The project's manager selects six; the site engineer may not configure the project.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/projects/{people.ProjectId}/catalog-selection", people.Engineer,
            new { masterItemIds = new[] { cement } })).StatusCode);
        var added = await SelectAsync(api, people.Manager, people.ProjectId, concrete, cement, steel, plumbing, electrical, transport);
        Assert.Equal(6, added.Count);

        // Selecting again changes nothing: the same records, no duplicates.
        var again = await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{people.ProjectId}/catalog-selection",
            new { masterItemIds = new[] { concrete, cement, steel, plumbing, electrical, transport } });
        Assert.Empty(again.GetProperty("added").EnumerateArray());
        Assert.Equal(new[] { cement, steel, transport, concrete, plumbing, electrical }.Order(), again.GetProperty("alreadySelected").EnumerateArray().Select(id => id.GetInt32()));

        // Work items became project items ("بنود"), priced items became catalog items, each linked to its master.
        var items = (await CoreApi.ItemsAsync(api, people.Manager, people.ProjectId)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Concrete work", "Electrical work", "Plumbing"], items.Select(item => item.GetProperty("nameEn").GetString()).Order());
        Assert.All(items, item => Assert.Contains(item.GetProperty("masterItemId").GetInt32(), new[] { concrete, plumbing, electrical }));
        var catalog = (await Get(api, people.Manager, $"/api/projects/{people.ProjectId}/catalog-items")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Cement", "Reinforcing steel", "Transport of materials"], catalog.Select(item => item.GetProperty("nameEn").GetString()).Order());
        Assert.DoesNotContain(catalog, item => item.GetProperty("nameEn").GetString() == "Sand");

        // Prices are not copied: the project reads the master's controlled price until it has one of its own.
        var cementOnProject = catalog.Single(item => item.GetProperty("masterItemId").GetInt32() == cement);
        Assert.Equal((20m, "Master"), (Money(cementOnProject, "currentPrice"), cementOnProject.GetProperty("priceSource").GetString()));

        // The master catalog is still seven rows; the selection screen marks what this project uses.
        Assert.Equal(7, (await Get(api, people.Accountant, "/api/catalog/items")).GetProperty("totalCount").GetInt32());
        var selection = (await Get(api, people.Manager, $"/api/projects/{people.ProjectId}/catalog-selection")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(6, selection.Count(row => row.GetProperty("selected").GetBoolean()));
        Assert.False(selection.Single(row => row.GetProperty("masterItemId").GetInt32() == sand).GetProperty("selected").GetBoolean());
        Assert.Equal(1, (await Get(api, people.Accountant, $"/api/catalog/items/{cement}")).GetProperty("item").GetProperty("projectCount").GetInt32());

        // A master item's unit can't change once a project uses it.
        var cementItem = (await Get(api, ex, $"/api/catalog/items/{cement}")).GetProperty("item");
        Assert.Equal("master_item_in_use", await Refused(api, ex, HttpMethod.Put, $"/api/catalog/items/{cement}", new
        {
            nameAr = "صنف Cement", nameEn = "Cement", kind = "Material", category = "CementAggregates", unit = "Ton", rowVersion = Version(cementItem),
        }, HttpStatusCode.Conflict));
    }

    [DatabaseFact]
    public async Task C_a_price_above_or_below_the_reference_waits_for_the_financial_manager_and_never_changes_the_catalog()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api);
        var project = people.ProjectId;
        var cement = Id(await MasterItemAsync(api, people.Executive, "Cement", "Material", "CementAggregates", "Bag", price: 1m));
        var concrete = Id(await MasterItemAsync(api, people.Executive, "Concrete work", "Composite", "Concrete", "M3"));
        var selected = await SelectAsync(api, people.Manager, project, cement, concrete);
        var supplier = await CoreApi.CreateSupplierAsync(api, people.Executive, "Cemex");

        Dictionary<string, object?> Supply(decimal unitPrice, string date = "2026-01-10") => CoreApi.SupplyBody(supplier, 100m, unitPrice, "Credit", new()
        {
            ["catalogItemId"] = selected[cement], ["projectItemId"] = selected[concrete], ["expenseDate"] = date,
        });

        // Exact price and a one-piastre rounding difference pass; 2 EGP against 1 EGP and 0.50 EGP don't.
        var exact = await CoreApi.RecordAsync(api, people.Engineer, project, Supply(1m));
        var rounded = await CoreApi.RecordAsync(api, people.Engineer, project, Supply(1.01m));
        Assert.Equal(JsonValueKind.Null, exact.GetProperty("priceVariance").ValueKind);
        Assert.Equal(JsonValueKind.Null, rounded.GetProperty("priceVariance").ValueKind);
        var above = await CoreApi.RecordAsync(api, people.Engineer, project, Supply(2m));
        var below = await CoreApi.RecordAsync(api, people.Engineer, project, Supply(0.5m));
        var aboveVariance = above.GetProperty("priceVariance");
        Assert.Equal(("Open", 1m, 2m, 1m, 100m, "Above"), (aboveVariance.GetProperty("status").GetString(), Money(aboveVariance, "referencePrice"),
            Money(aboveVariance, "actualPrice"), Money(aboveVariance, "unitVariance"), Money(aboveVariance, "variancePercent"), aboveVariance.GetProperty("direction").GetString()));
        // The total variance is money: hidden from the engineer, shown to the Accountant.
        Assert.Equal(JsonValueKind.Null, aboveVariance.GetProperty("totalVariance").ValueKind);
        Assert.Equal(100m, Money((await CoreApi.GetExpenseAsync(api, people.Accountant, project, Id(above))).GetProperty("priceVariance"), "totalVariance"));
        Assert.Equal("Below", below.GetProperty("priceVariance").GetProperty("direction").GetString());

        // The Financial Manager hears about it; the engineer who bought is not accused, only asked to wait.
        var varianceId = aboveVariance.GetProperty("id").GetInt32();
        await WaitForNotificationAsync(api, people.FinancialManager, About("ReviewRequested", "PriceVariance", varianceId));
        Assert.DoesNotContain(await NotificationsAsync(api, people.Engineer), notification => About("ReviewRequested", "PriceVariance", varianceId)(notification));

        // The review can't be skipped: approving the supply while its price waits is refused.
        Assert.Equal("price_variance_open", await Refused(api, people.Accountant, HttpMethod.Post, $"/api/projects/{project}/expenses/{Id(above)}/approve",
            new { rowVersion = Version(above) }, HttpStatusCode.Conflict));
        await CoreApi.ApproveAsync(api, people.Accountant, project, exact);

        // Only catalog Approve decides, never the buyer; the Accountant can't either.
        var variance = await VarianceAsync(api, people.FinancialManager, project, varianceId);
        var decide = $"/api/projects/{project}/price-variances/{varianceId}";
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"{decide}/accept", people.Engineer, new { note = "ok", rowVersion = Version(variance) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"{decide}/accept", people.Accountant, new { note = "ok", rowVersion = Version(variance) })).StatusCode);

        // Rejected: the supply still can't be approved; fixed to the reference price, it can.
        var rejected = await Send(api, people.FinancialManager, HttpMethod.Post, $"{decide}/reject", new { note = "Use the framework supplier price", rowVersion = Version(variance) });
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        await WaitForNotificationAsync(api, people.Engineer, About("RecordRejected", "PriceVariance", varianceId));
        Assert.Equal("price_variance_open", await Refused(api, people.Accountant, HttpMethod.Post, $"/api/projects/{project}/expenses/{Id(above)}/approve",
            new { rowVersion = Version(above) }, HttpStatusCode.Conflict));
        var fixedSupply = await Send(api, people.Engineer, HttpMethod.Put, $"/api/projects/{project}/expenses/{Id(above)}",
            CoreApi.EditBody(await CoreApi.GetExpenseAsync(api, people.Engineer, project, Id(above)), new() { ["unitPrice"] = 1m }));
        Assert.Equal(JsonValueKind.Null, fixedSupply.GetProperty("priceVariance").ValueKind);
        Assert.Equal("Superseded", (await VarianceAsync(api, people.FinancialManager, project, varianceId)).GetProperty("status").GetString());
        await CoreApi.ApproveAsync(api, people.Accountant, project, fixedSupply);

        // Accepted: this supply only, at its own price. Deciding twice is refused.
        var belowVariance = await VarianceAsync(api, people.FinancialManager, project, below.GetProperty("priceVariance").GetProperty("id").GetInt32());
        var accepted = await Send(api, people.FinancialManager, HttpMethod.Post, $"/api/projects/{project}/price-variances/{Id(belowVariance)}/accept",
            new { note = "End-of-stock discount, confirmed with the supplier", rowVersion = Version(belowVariance) });
        Assert.Equal("Accepted", accepted.GetProperty("status").GetString());
        Assert.Equal("price_variance_decided", await Refused(api, people.FinancialManager, HttpMethod.Post,
            $"/api/projects/{project}/price-variances/{Id(belowVariance)}/accept", new { note = "again", rowVersion = Version(accepted) }, HttpStatusCode.Conflict));
        var approvedBelow = await CoreApi.ApproveAsync(api, people.Accountant, project, await CoreApi.GetExpenseAsync(api, people.Engineer, project, Id(below)));
        Assert.Equal(50m, Money(approvedBelow, "amount"));

        // The catalog never moved, and the next supply at 2 EGP is checked again.
        Assert.Equal(1m, Money((await Get(api, people.Accountant, $"/api/catalog/items/{cement}")).GetProperty("item"), "currentPrice"));
        var next = await CoreApi.RecordAsync(api, people.Engineer, project, Supply(2m, "2026-01-11"));
        Assert.Equal("Open", next.GetProperty("priceVariance").GetProperty("status").GetString());

        // Recognized cost: exact 100 + rounded (not yet approved) + fixed 100 + below 50 = 250; nothing paid yet.
        var overview = await Get(api, people.Accountant, $"/api/projects/{project}/overview");
        Assert.Equal((250m, 0m), (Money(Group(overview, "Materials"), "recognized"), Money(Group(overview, "Materials"), "paid")));

        // The thresholds are the Executive's: the Financial Manager can't loosen them.
        var policy = await Get(api, people.FinancialManager, "/api/catalog/pricing-policy");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Put, "/api/catalog/pricing-policy", people.FinancialManager,
            new { varianceTolerancePercent = 50m, roundingAllowance = 0.01m, rowVersion = Version(policy) })).StatusCode);
        await Send(api, people.Executive, HttpMethod.Put, "/api/catalog/pricing-policy", new { varianceTolerancePercent = 10m, roundingAllowance = 0.01m, rowVersion = Version(policy) });
        var withinTenPercent = await CoreApi.RecordAsync(api, people.Engineer, project, Supply(1.08m, "2026-01-12"));
        Assert.Equal(JsonValueKind.Null, withinTenPercent.GetProperty("priceVariance").ValueKind);
    }

    [DatabaseFact]
    public async Task D_an_accountant_asks_the_financial_manager_approves_and_old_supplies_keep_their_price()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api);
        var project = people.ProjectId;
        var steel = Id(await MasterItemAsync(api, people.Executive, "Reinforcing steel", "Material", "Steel", "Ton", price: 40_000m));
        var selected = await SelectAsync(api, people.Manager, project, steel);
        var supplier = await CoreApi.CreateSupplierAsync(api, people.Executive, "Ezz Steel");

        Dictionary<string, object?> Supply(decimal unitPrice, string date) => CoreApi.SupplyBody(supplier, 2m, unitPrice, "Credit", new()
        {
            ["catalogItemId"] = selected[steel], ["expenseDate"] = date, ["unit"] = "Ton", ["itemName"] = "Steel 16 mm",
        });

        var january = await CoreApi.RecordApprovedAsync(api, people.Engineer, people.Accountant, project, Supply(40_000m, "2026-01-10"));
        Assert.Equal(40_000m, Money(january, "catalogPriceAtRecording"));

        // Nobody changes an approved master price directly: the edit form has no price, and only catalog Create edits it.
        var itemNow = (await Get(api, people.Executive, $"/api/catalog/items/{steel}")).GetProperty("item");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Put, $"/api/catalog/items/{steel}", people.Accountant, new
        {
            nameAr = "صنف Reinforcing steel", nameEn = "Reinforcing steel", kind = "Material", category = "Steel", unit = "Ton", rowVersion = Version(itemNow),
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/catalog/items/{steel}/price-changes", people.Engineer,
            new { price = 1m, reason = "x" })).StatusCode);

        // The Accountant asks, with a reason, from 1 February.
        var request = await Send(api, people.Accountant, HttpMethod.Post, $"/api/catalog/items/{steel}/price-changes",
            new { price = 42_000m, effectiveFrom = "2026-02-01", reason = "Ezz Steel price list of February" }, HttpStatusCode.Created);
        Assert.Equal(("Pending", 40_000m), (request.GetProperty("status").GetString(), Money(request, "previousPrice")));
        var requestId = Id(request);
        await WaitForNotificationAsync(api, people.FinancialManager, About("ReviewRequested", "MasterItemPrice", requestId));
        var queue = await Get(api, people.FinancialManager, "/api/catalog/price-changes?status=Pending");
        Assert.Equal([requestId], queue.GetProperty("items").EnumerateArray().Select(Id));

        // Not the requester, not a role without catalog Approve.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/catalog/price-changes/{requestId}/approve", people.Accountant, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/catalog/price-changes/{requestId}/approve", people.Manager, new { })).StatusCode);

        var approved = await Send(api, people.FinancialManager, HttpMethod.Post, $"/api/catalog/price-changes/{requestId}/approve", new { note = "Matches the market" });
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        await WaitForNotificationAsync(api, people.Accountant, About("RecordApproved", "MasterItemPrice", requestId));

        // The effective date decides: a January supply still uses 40,000; a February one 42,000.
        var lateJanuary = await CoreApi.RecordAsync(api, people.Engineer, project, Supply(40_000m, "2026-01-25"));
        var february = await CoreApi.RecordAsync(api, people.Engineer, project, Supply(42_000m, "2026-02-05"));
        Assert.Equal((40_000m, 42_000m), (Money(lateJanuary, "catalogPriceAtRecording"), Money(february, "catalogPriceAtRecording")));
        Assert.Equal(JsonValueKind.Null, february.GetProperty("priceVariance").ValueKind);

        // History is never rewritten.
        var januaryNow = await CoreApi.GetExpenseAsync(api, people.Accountant, project, Id(january));
        Assert.Equal((40_000m, 80_000m), (Money(januaryNow, "catalogPriceAtRecording"), Money(januaryNow, "amount")));

        // An Executive's own request still needs someone else.
        var own = await Send(api, people.Executive, HttpMethod.Post, $"/api/catalog/items/{steel}/price-changes",
            new { price = 43_000m, effectiveFrom = "2026-03-01", reason = "March list" }, HttpStatusCode.Created);
        Assert.Equal("own_record", await Refused(api, people.Executive, HttpMethod.Post, $"/api/catalog/price-changes/{Id(own)}/approve", new { }, HttpStatusCode.Conflict));
        Assert.Equal("price_change_pending", await Refused(api, people.Accountant, HttpMethod.Post, $"/api/catalog/items/{steel}/price-changes",
            new { price = 44_000m, reason = "another" }, HttpStatusCode.Conflict));
    }

    [DatabaseFact]
    public async Task E_a_composite_item_breaks_its_own_cost_into_components_and_the_project_counts_each_record_once()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api);
        var project = people.ProjectId;
        var ex = people.Executive;
        var cement = Id(await MasterItemAsync(api, ex, "Cement", "Material", "CementAggregates", "Bag", price: 20m));
        var sand = Id(await MasterItemAsync(api, ex, "Sand", "Material", "CementAggregates", "M3", price: 150m));
        var aggregate = Id(await MasterItemAsync(api, ex, "Aggregate", "Material", "CementAggregates", "M3", price: 200m));
        var concrete = Id(await MasterItemAsync(api, ex, "Concrete work", "Composite", "Concrete", "M3"));
        var plastering = Id(await MasterItemAsync(api, ex, "Plastering", "Work", "Plastering", "M2"));
        var on = await SelectAsync(api, people.Manager, project, cement, sand, aggregate, concrete, plastering);
        var supplier = await CoreApi.CreateSupplierAsync(api, ex, "Cemex");

        // The project's own definition of concrete: three materials, labor and equipment.
        var components = await Send(api, people.Manager, HttpMethod.Put, $"/api/projects/{project}/items/{on[concrete]}/components", new
        {
            components = new object[]
            {
                new { kind = "Material", catalogItemId = on[cement], plannedQuantity = 700m, quantityPerUnit = 7m },
                new { kind = "Material", catalogItemId = on[sand], plannedQuantity = 50m },
                new { kind = "Material", catalogItemId = on[aggregate], plannedQuantity = 80m },
                new { kind = "Labor" },
                new { kind = "Equipment" },
            },
        });
        Assert.Equal(5, components.GetArrayLength());

        async Task<JsonElement> Bought(int catalogItemId, string unit, decimal quantity, decimal price, int? itemId) =>
            await CoreApi.RecordApprovedAsync(api, people.Engineer, people.Accountant, project, CoreApi.SupplyBody(supplier, quantity, price, "Custody", new()
            {
                ["catalogItemId"] = catalogItemId, ["projectItemId"] = itemId, ["unit"] = unit, ["itemName"] = $"Item {catalogItemId}",
            }));

        await Bought(on[cement], "Bag", 100m, 20m, on[concrete]);                 // 2,000
        var sandSupply = await Bought(on[sand], "M3", 10m, 150m, on[concrete]);   // 1,500
        await Bought(on[aggregate], "M3", 10m, 200m, on[concrete]);               // 2,000
        await CoreApi.RecordApprovedAsync(api, people.Engineer, people.Accountant, project, CoreApi.SupplyBody(supplier, 1m, 300m, "Custody", new()
        {
            ["projectItemId"] = on[concrete], ["itemName"] = "Admixture", ["unit"] = "Lot",
        }));                                                                       // 300, no component: "other"

        // Labor: two workers a day on concrete (800). Equipment: mixer fuel (500), linked to concrete.
        var labor = new WorkforceApi.Cast(people.Executive, people.Manager, people.Engineer, people.EngineerId, people.Accountant, project, people.ManagerId);
        var mason1 = await WorkforceApi.HireAsync(api, ex, project, WorkforceApi.Mason, "Saad", dailyRate: 400m);
        var mason2 = await WorkforceApi.HireAsync(api, ex, project, WorkforceApi.Mason, "Fathy", dailyRate: 400m);
        await WorkforceApi.RecordApprovedSheetAsync(api, labor, project, WorkforceApi.Today(api).AddDays(-1),
            WorkforceApi.Line(mason1, 1m, [(on[concrete], 1m)]), WorkforceApi.Line(mason2, 1m, [(on[concrete], 1m)]));
        var mixer = await FleetApi.RegisterAsync(api, ex, FleetApi.EquipmentBody("CF-MIX-1"));
        await FleetApi.AssignedAsync(api, people.Manager, project, Id(mixer), "2026-01-05");
        var fuel = await FleetApi.ReviewedAsync(api, people.Accountant, project,
            await FleetApi.RecordedCostAsync(api, people.Manager, project, FleetApi.FuelBody(Id(mixer), 25m, 20m)), "approve");
        await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/equipment-costs/{Id(fuel)}/item", new { projectItemId = on[concrete], rowVersion = Version(fuel) });

        // The composite rolls up: components + other = the item's cost = 7,100.
        var detail = await CoreApi.ItemDetailAsync(api, people.Accountant, project, on[concrete]);
        var breakdown = detail.GetProperty("components");
        var byComponent = breakdown.GetProperty("components").EnumerateArray()
            .ToDictionary(row => row.GetProperty("kind").GetString() + ":" + (row.GetProperty("catalogItemNameEn").GetString() ?? ""), row => Money(row, "cost"));
        Assert.Equal(2_000m, byComponent["Material:Cement"]);
        Assert.Equal(1_500m, byComponent["Material:Sand"]);
        Assert.Equal(2_000m, byComponent["Material:Aggregate"]);
        Assert.Equal(800m, byComponent["Labor:"]);
        Assert.Equal(500m, byComponent["Equipment:"]);
        Assert.Equal(300m, Money(breakdown.GetProperty("other"), "cost"));
        Assert.Equal(7_100m, Money(breakdown.GetProperty("total"), "cost"));
        Assert.Equal(7_100m, Money(detail.GetProperty("item"), "totalCost"));
        Assert.Equal(100m, breakdown.GetProperty("components").EnumerateArray()
            .Single(row => row.GetProperty("catalogItemNameEn").GetString() == "Cement").GetProperty("actualQuantity").GetDecimal());

        // Counted once: the project's recognized cost is the same 7,100, not 7,100 + the components again.
        var overview = await Get(api, people.Accountant, $"/api/projects/{project}/overview");
        Assert.Equal(7_100m, Money(overview.GetProperty("totals"), "recognized"));
        var items = await CoreApi.ItemsAsync(api, people.Accountant, project);
        Assert.Equal(7_100m, Money(items.GetProperty("totals"), "totalCost"));
        Assert.Equal(7_100m, Money(items.GetProperty("reconciliation"), "projectRecognizedCost"));

        // Moving the sand to plastering moves 1,500 between items; the project total doesn't move.
        var sandNow = await CoreApi.GetExpenseAsync(api, people.Accountant, project, Id(sandSupply));
        await AccessApi.EnsureStatus(await CoreApi.LinkAsync(api, people.Manager, project, sandNow, on[plastering]), HttpStatusCode.OK);
        var concreteAfter = await CoreApi.ItemDetailAsync(api, people.Accountant, project, on[concrete]);
        Assert.Equal(5_600m, Money(concreteAfter.GetProperty("item"), "totalCost"));
        Assert.Equal(0m, concreteAfter.GetProperty("components").GetProperty("components").EnumerateArray()
            .Single(row => row.GetProperty("catalogItemNameEn").GetString() == "Sand").GetProperty("cost").GetDecimal());
        Assert.Equal(1_500m, Money((await CoreApi.ItemDetailAsync(api, people.Accountant, project, on[plastering])).GetProperty("item"), "totalCost"));
        Assert.Equal(7_100m, Money((await Get(api, people.Accountant, $"/api/projects/{project}/overview")).GetProperty("totals"), "recognized"));
    }

    private static async Task<JsonElement> VarianceAsync(ProjectsApi api, Session session, int projectId, int varianceId) =>
        (await Get(api, session, $"/api/projects/{projectId}/price-variances?pageSize=100")).GetProperty("items").EnumerateArray().Single(row => Id(row) == varianceId);
}
