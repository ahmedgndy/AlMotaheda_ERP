using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using AlMotaheda.Modules.Projects.Tests.Database.PriceControl;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.CostManagement;

/// <summary>
/// Project cost management on SQL Server: main items and sub-items, the company price list and the price check of a
/// purchase, the work-item breakdown, retries, the price policies and who may do what.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CostManagementTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Main_items_hold_sub_items_of_the_same_project_two_levels_deep_with_names_unique_among_siblings()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var concrete = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works");
        var cement = await api.CreateSubItemAsync(cast.Manager, cast.ProjectId, concrete.Id(), "Cement");
        var steel = await api.CreateSubItemAsync(cast.Manager, cast.ProjectId, concrete.Id(), "Steel reinforcement");
        var masonry = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Masonry works");

        Assert.Equal(concrete.Id(), cement.GetProperty("parentItemId").GetInt32());
        Assert.Equal(2, cement.GetProperty("level").GetInt32());
        Assert.Equal(JsonValueKind.Null, cement.GetProperty("withSubItems").ValueKind);

        // The same sub-item name under another main item is fine; twice under the same one is not.
        await api.CreateSubItemAsync(cast.Manager, cast.ProjectId, masonry.Id(), "Cement");
        var twice = await api.PostSubItemAsync(cast.Manager, cast.ProjectId, concrete.Id(), "Cement");
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("item_name_taken", await CodeOf(twice));

        // Two levels only, and never a parent from another project.
        var third = await api.PostSubItemAsync(cast.Manager, cast.ProjectId, cement.Id(), "Bags");
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal("item_parent_is_sub_item", await CodeOf(third));
        var otherProject = (await api.CreateProjectAsync(cast.Executive, "OTHER-PRJ", cast.Executive.UserId)).Id();
        var foreign = await api.CreateItemAsync(cast.Executive, otherProject, "Foreign main");
        var crossProject = await api.PostSubItemAsync(cast.Manager, cast.ProjectId, foreign.Id(), "Smuggled");
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);
        Assert.Contains("parentItemId", await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(crossProject));

        // Screen order: each main item followed by its sub-items.
        var table = await api.ItemsAsync(cast.Executive, cast.ProjectId);
        Assert.Equal(
            ["Concrete works", "Cement", "Steel reinforcement", "Masonry works", "Cement"],
            table.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("nameEn").GetString()));
        var concreteRow = table.GetProperty("items").EnumerateArray().First();
        Assert.Equal(2, concreteRow.GetProperty("subItemCount").GetInt32());

        // A main item with sub-items can't be deleted, deactivated first, or moved under another item.
        var delete = await api.SendAsync(HttpMethod.Delete, $"/api/projects/{cast.ProjectId}/items/{concrete.Id()}", cast.Manager);
        Assert.Equal("item_has_sub_items", await CodeOf(delete));
        var deactivate = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items/{concrete.Id()}/deactivate", cast.Manager,
            new { rowVersion = concreteRow.GetProperty("rowVersion").GetString() });
        Assert.Equal("item_has_active_sub_items", await CodeOf(deactivate));
        var moveMain = await api.MoveAsync(cast.Manager, cast.ProjectId, concreteRow, masonry.Id());
        Assert.Equal("item_has_sub_items", await CodeOf(moveMain));

        // An existing flat item is organised under a main item, and made a main item again.
        var plaster = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Plastering");
        var moved = await api.MoveAsync(cast.Manager, cast.ProjectId, plaster, masonry.Id());
        await EnsureStatus(moved, HttpStatusCode.OK);
        var movedRow = await Json(moved);
        Assert.Equal(masonry.Id(), movedRow.GetProperty("parentItemId").GetInt32());
        var back = await api.MoveAsync(cast.Manager, cast.ProjectId, movedRow, null);
        await EnsureStatus(back, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, (await Json(back)).GetProperty("parentItemId").ValueKind);

        // Only the manager or an Executive organises the items.
        var byEngineer = await api.PostSubItemAsync(cast.Engineer, cast.ProjectId, concrete.Id(), "Engineer's");
        Assert.Equal(HttpStatusCode.Forbidden, byEngineer.StatusCode);
        _ = steel;
    }

    [DatabaseFact]
    public async Task Cement_bought_above_its_reference_range_is_warned_justified_alerted_and_lands_once_in_the_work_item_breakdown()
    {
        // The warning policy: the case is opened for the inbox and the reports, and gates nothing (the shipped default gates).
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:PriceCheckPolicy"] = "Warn" });
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var concrete = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works");
        var cement = await api.CreateSubItemAsync(cast.Manager, cast.ProjectId, concrete.Id(), "Cement");
        var priceItem = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);

        // 2 tons at 1,400: refused without a reason, recorded with one.
        var purchase = CementPurchase(supplier, priceItem, cement.Id(), unitPrice: 1_400m);
        var unexplained = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer, purchase);
        Assert.Equal(HttpStatusCode.BadRequest, unexplained.StatusCode);
        Assert.Equal(["priceJustification"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(unexplained));

        purchase["priceJustification"] = "The only supplier with stock for Thursday's pour";
        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, purchase);
        var check = recorded.GetProperty("priceCheck");
        Assert.Equal("AboveRange", check.GetProperty("result").GetString());
        Assert.Equal((1_000m, 1_200m, 1_400m), (check.Money("minPrice"), check.Money("maxPrice"), check.Money("unitPrice")));
        Assert.Equal((200m, 400m), (check.Money("differencePerUnit"), check.Money("totalDifference")));
        Assert.Equal(2_800m, recorded.Money("amount"));

        // A price exception case is opened in the record's own save (the Warn policy: for the inbox and the reports, no gate);
        // the manager, the Executive and the accountant are alerted about it, the manager about the new cost.
        Assert.Equal("Pending", check.GetProperty("exception").GetProperty("status").GetString());
        var alerts = await api.QueryAsync(db => db.CostAlerts.Where(alert => alert.ExpenseId == recorded.Id())
            .Select(alert => new { Kind = EF.Property<string>(alert, "Kind"), alert.RecipientUserId }).ToListAsync());
        Assert.Single(alerts, alert => alert.Kind == "CostRecorded");
        var told = await api.QueryAsync(db => db.PriceExceptionAlerts.Where(alert => alert.Exception.SourceId == recorded.Id()).Select(alert => alert.RecipientUserId).ToListAsync());
        Assert.Equal(3, told.Count);
        Assert.DoesNotContain(cast.Engineer.UserId, told);

        // A unit the price list item isn't priced in can't be compared, so it is refused.
        var perBag = CementPurchase(supplier, priceItem, cement.Id(), unitPrice: 70m, unit: "Bag");
        var bags = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer, perBag);
        Assert.Equal(["unit"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(bags));

        // A later range never rewrites the comparison already made.
        await api.ChangeRangeAsync(cast.Manager, cast.Executive, priceItem, 1_300m, 1_500m);
        var after = await api.GetExpenseAsync(cast.Executive, cast.ProjectId, recorded.Id());
        Assert.Equal(1_200m, after.GetProperty("priceCheck").Money("maxPrice"));

        // Not a cost until approved; then it is in the sub-item, in the main item's roll-up, and owed to the supplier.
        var pending = await api.BreakdownAsync(cast.Executive, cast.ProjectId, concrete.Id());
        Assert.Equal(0m, pending.GetProperty("totals").Money("totalCost"));
        Assert.Equal(1, pending.GetProperty("pending").GetProperty("supplierRecordCount").GetInt32());

        var approved = await api.ApproveAsync(cast.Accountant, cast.ProjectId, after);
        var breakdown = await api.BreakdownAsync(cast.Executive, cast.ProjectId, concrete.Id());
        Assert.Equal(2_800m, breakdown.GetProperty("totals").Money("totalCost"));
        Assert.Equal(2_800m, breakdown.GetProperty("filtered").Money("amount"));
        var line = Assert.Single(breakdown.GetProperty("transactions").EnumerateArray());
        Assert.Equal((cement.Id(), "Supply"), (line.GetProperty("itemId").GetInt32(), line.GetProperty("type").GetString()));
        var owed = Assert.Single(breakdown.GetProperty("suppliers").EnumerateArray());
        Assert.Equal((2_800m, 0m, 2_800m), (owed.Money("invoiced"), owed.Money("paid"), owed.Money("outstanding")));

        // A payment of 1,000 is allocated to the invoice: 1,800 still owed. Totals count the record once.
        await api.PaidAsync(cast.Accountant, cast.ProjectId, supplier, 1_000m, (approved.Id(), 1_000m));
        var paid = await api.BreakdownAsync(cast.Executive, cast.ProjectId, concrete.Id());
        var balance = Assert.Single(paid.GetProperty("suppliers").EnumerateArray());
        Assert.Equal((2_800m, 1_000m, 1_800m), (balance.Money("invoiced"), balance.Money("paid"), balance.Money("outstanding")));
        var items = await api.ItemsAsync(cast.Executive, cast.ProjectId);
        Assert.Equal(2_800m, items.GetProperty("totals").Money("totalCost"));
        var mainRow = items.GetProperty("items").EnumerateArray().Single(row => row.Id() == concrete.Id());
        Assert.Equal(0m, mainRow.Money("totalCost"));
        Assert.Equal(2_800m, mainRow.GetProperty("withSubItems").Money("totalCost"));
        Assert.Equal(1_800m, mainRow.GetProperty("withSubItems").Money("outstanding"));

        // The sub-item filter and a foreign sub-item.
        var onlyCement = await api.BreakdownAsync(cast.Executive, cast.ProjectId, concrete.Id(), $"?subItemId={cement.Id()}&type=Supply");
        Assert.Single(onlyCement.GetProperty("transactions").EnumerateArray());
        var wrongSub = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/items/{cement.Id()}/cost-breakdown?subItemId={concrete.Id()}", cast.Executive);
        Assert.Equal(["subItemId"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(wrongSub));
    }

    [DatabaseFact]
    public async Task The_same_save_sent_twice_records_one_purchase()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var key = Guid.NewGuid().ToString();
        var body = SupplyBody(supplier, 10m, 100m, "Credit", new() { ["idempotencyKey"] = key });

        var first = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer, body);
        var retry = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer, body);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await Json(first)).Id(), (await Json(retry)).Id());
        Assert.Equal(1, await api.QueryAsync(db => db.ProjectExpenses.CountAsync()));

        // The same key from someone else is not a retry.
        var stolen = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Manager, body);
        Assert.Equal(HttpStatusCode.Conflict, stolen.StatusCode);
        Assert.Equal("idempotency_key_reused", await CodeOf(stolen));
        Assert.Equal(1, await api.QueryAsync(db => db.ProjectExpenses.CountAsync()));
    }

    [DatabaseFact]
    public async Task Under_the_executive_approval_policy_only_an_executive_approves_a_purchase_above_its_range()
    {
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:PriceCheckPolicy"] = "ExecutiveApproval" });
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var priceItem = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);
        var purchase = CementPurchase(supplier, priceItem, null, 1_400m);
        purchase["priceJustification"] = "Urgent";
        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, purchase);

        // Nobody approves the record, not even an Executive, while its price exception waits for the Executive's decision.
        var exceptionId = recorded.GetProperty("priceCheck").GetProperty("exception").Id();
        foreach (var reviewer in new[] { cast.Accountant, cast.Executive })
        {
            var early = await api.ReviewAsync(reviewer, cast.ProjectId, recorded, "approve");
            Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
            Assert.Equal("price_exception_pending", await CodeOf(early));
        }

        // The Executive approves the price once; then the accountant approves the record as usual.
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/price-exceptions/{exceptionId}/approve", cast.Executive,
            new { note = "Urgent pour, accepted this once", rowVersion = (await api.ExceptionAsync(cast.Executive, exceptionId)).GetProperty("rowVersion").GetString() }), HttpStatusCode.OK);
        var approved = await api.ApproveAsync(cast.Accountant, cast.ProjectId, await api.GetExpenseAsync(cast.Accountant, cast.ProjectId, recorded.Id()));
        Assert.Equal("Approved", approved.GetProperty("status").GetString());

        // Within the range, the accountant approves as before.
        var normal = await api.RecordAsync(cast.Engineer, cast.ProjectId, CementPurchase(supplier, priceItem, null, 1_100m));
        await api.ApproveAsync(cast.Accountant, cast.ProjectId, normal);
    }

    [DatabaseFact]
    public async Task Under_the_block_policy_a_purchase_above_its_range_is_refused_and_nothing_is_recorded()
    {
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:PriceCheckPolicy"] = "Block" });
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var priceItem = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);
        var purchase = CementPurchase(supplier, priceItem, null, 1_400m);
        purchase["priceJustification"] = "Urgent";

        var refused = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer, purchase);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("price_above_range", await CodeOf(refused));
        Assert.Equal(0, await api.QueryAsync(db => db.ProjectExpenses.CountAsync()));
        Assert.Equal(0, await api.QueryAsync(db => db.CostAlerts.CountAsync()));
    }

    [DatabaseFact]
    public async Task Price_ranges_are_decided_by_another_executive_and_supplier_ranges_come_first()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var priceItem = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);

        // The engineer sees prices but can't set them; the manager asks, the manager can't decide.
        var byEngineer = await api.SendAsync(HttpMethod.Post, "/api/price-items", cast.Engineer, CostManagementApi.PriceItemBody("Sand", "M3", 100m, 150m));
        Assert.Equal(HttpStatusCode.Forbidden, byEngineer.StatusCode);
        await EnsureStatus(await api.SendAsync(HttpMethod.Get, "/api/price-items", cast.Engineer), HttpStatusCode.OK);

        var request = await api.SendAsync(HttpMethod.Post, $"/api/price-items/{priceItem}/ranges", cast.Manager, new
        {
            supplierId = supplier, minPrice = 900m, maxPrice = 1_000m, effectiveFrom = "2026-01-01", reason = "Annual agreement with Cemex",
        });
        await EnsureStatus(request, HttpStatusCode.Created);
        var range = await Json(request);
        var again = await api.SendAsync(HttpMethod.Post, $"/api/price-items/{priceItem}/ranges", cast.Manager, new
        {
            supplierId = supplier, minPrice = 950m, maxPrice = 1_050m, effectiveFrom = "2026-01-02", reason = "Again",
        });
        Assert.Equal("price_change_pending", await CodeOf(again));

        var byManager = await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{range.Id()}/approve", cast.Manager, new { });
        Assert.Equal(HttpStatusCode.Forbidden, byManager.StatusCode);
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{range.Id()}/approve", cast.Executive, new { }), HttpStatusCode.OK);

        // Cemex's own range applies to Cemex: 1,100 is above it, while the general range would accept it.
        var forCemex = await Json(await api.SendAsync(HttpMethod.Get, $"/api/price-items/{priceItem}/check?unitPrice=1100&quantity=2&date=2026-01-10&supplierId={supplier}", cast.Engineer));
        Assert.Equal(("AboveRange", 200m), (forCemex.GetProperty("result").GetString(), forCemex.Money("totalDifference")));
        var general = await Json(await api.SendAsync(HttpMethod.Get, $"/api/price-items/{priceItem}/check?unitPrice=1100&quantity=2&date=2026-01-10", cast.Engineer));
        Assert.Equal("WithinRange", general.GetProperty("result").GetString());

        // The Executive's own request needs another Executive.
        var own = await api.SendAsync(HttpMethod.Post, $"/api/price-items/{priceItem}/ranges", cast.Executive, new
        {
            minPrice = 1_050m, maxPrice = 1_250m, effectiveFrom = "2026-02-01", reason = "Market increase",
        });
        var ownRange = await Json(own);
        var selfApproval = await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{ownRange.Id()}/approve", cast.Executive, new { });
        Assert.Equal("own_record", await CodeOf(selfApproval));

        var history = await Json(await api.SendAsync(HttpMethod.Get, $"/api/price-items/{priceItem}", cast.Engineer));
        Assert.Equal(3, history.GetProperty("ranges").GetArrayLength());
    }

    [DatabaseFact]
    public async Task Another_project_and_money_stay_out_of_reach()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var (otherManagerId, otherManager) = await api.CreateSignedInUserAsync(cast.Executive, "manager");
        await api.CreateProjectAsync(cast.Executive, "PROJ-B", otherManagerId);
        var concrete = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works");

        var outsider = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/items/{concrete.Id()}/cost-breakdown", otherManager);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        var moveByOutsider = await api.MoveAsync(otherManager, cast.ProjectId, concrete, null);
        Assert.Equal(HttpStatusCode.NotFound, moveByOutsider.StatusCode);

        // The engineer has no See money: the breakdown is money.
        var engineer = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/items/{concrete.Id()}/cost-breakdown", cast.Engineer);
        Assert.Equal(HttpStatusCode.Forbidden, engineer.StatusCode);
    }

    private static Dictionary<string, object?> CementPurchase(int supplierId, int priceItemId, int? projectItemId, decimal unitPrice, string unit = "Ton") =>
        SupplyBody(supplierId, 2m, unitPrice, "Credit", new()
        {
            ["itemName"] = "Cement 42.5",
            ["unit"] = unit,
            ["priceItemId"] = priceItemId,
            ["projectItemId"] = projectItemId,
        });
}
