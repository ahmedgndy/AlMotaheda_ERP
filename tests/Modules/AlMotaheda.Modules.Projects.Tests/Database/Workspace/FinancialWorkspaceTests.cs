using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using AlMotaheda.Modules.Projects.Tests.Database.CostManagement;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Workspace;

/// <summary>
/// The project's money on SQL Server: cash custody (funding through Finance, a direct purchase paid from it, a
/// return, the balance policy), supply agreements, the project's suppliers and one supplier's account across
/// projects. Each test follows a business example and checks that every screen reads the same figures.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class FinancialWorkspaceTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_sand_purchase_of_200_paid_from_custody_costs_the_work_item_once_and_lowers_the_custody_once()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var team = await api.TeamAsync();
        var concrete = await api.CreateItemAsync(team.Manager, team.ProjectId, "Concrete works");

        // The funding is asked by the engineer, decided by the Executive: no money in the custody until it is paid.
        var asked = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/custody/fundings", team.Engineer,
            new { amount = 1_000m, method = "Cash", requestedDate = "2026-01-05", idempotencyKey = Guid.NewGuid().ToString() });
        await EnsureStatus(asked, HttpStatusCode.Created);
        var request = await Json(asked);
        Assert.Equal("CustodyFunding", request.GetProperty("sourceType").GetString());
        Assert.Equal(0m, await api.BalanceAsync(team.Engineer, team.ProjectId, team.EngineerId));
        await api.SettleAsync(team.Accountant, request);
        Assert.Equal(1_000m, await api.BalanceAsync(team.Engineer, team.ProjectId, team.EngineerId));

        // The purchase: 2 m3 of sand at 100 from a merchant, on Concrete works, paid from custody. Sent twice with the same key.
        var key = Guid.NewGuid();
        var body = FinancialWorkspaceApi.CashPurchaseBody("Sand", 2m, 100m, concrete.Id(), key);
        var first = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/expenses", team.Engineer, body);
        await EnsureStatus(first, HttpStatusCode.Created);
        var again = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/expenses", team.Engineer, body);
        await EnsureStatus(again, HttpStatusCode.OK);
        var sand = await Json(first);
        Assert.Equal(sand.Id(), (await Json(again)).Id());
        Assert.Equal(200m, sand.Money("amount"));
        Assert.Equal("Sand merchant", sand.GetProperty("payeeName").GetString());

        // Cash is gone at once (waiting for review), counted once; not a cost until someone else approves it.
        var pending = (await api.CustodyAsync(team.Engineer, team.ProjectId, team.EngineerId)).GetProperty("custody");
        Assert.Equal(800m, pending.Dec("balance"));
        Assert.Equal(200m, pending.Dec("spentPending"));
        Assert.Equal(0m, (await api.ItemRowAsync(team.Manager, team.ProjectId, concrete.Id())).Dec("totalCost"));

        // The manager, the Executives and the accountants are alerted, in the record's own transaction; never the engineer.
        var told = await api.QueryAsync(db => db.CostAlerts.OfType<CostRecordedAlert>().Where(alert => alert.ExpenseId == sand.Id())
            .Select(alert => alert.RecipientUserId).ToListAsync());
        Assert.Contains(team.ManagerId, told);
        Assert.Contains(team.AccountantId, told);
        Assert.Contains(team.Executive.UserId, told);
        Assert.DoesNotContain(team.EngineerId, told);

        // Approved by the accountant: the work item costs 200 (a direct purchase, paid), the custody still 800.
        await api.ApproveAsync(team.Accountant, team.ProjectId, sand);
        var row = await api.ItemRowAsync(team.Manager, team.ProjectId, concrete.Id());
        Assert.Equal(200m, row.Dec("totalCost"));
        Assert.Equal(200m, row.Dec("directCost"));
        Assert.Equal(200m, row.Dec("paid"));
        Assert.Equal(0m, row.Dec("outstanding"));
        var breakdown = await api.BreakdownAsync(team.Manager, team.ProjectId, concrete.Id());
        Assert.Equal("DirectPurchase", Assert.Single(breakdown.GetProperty("transactions").EnumerateArray()).GetProperty("type").GetString());

        var account = await api.CustodyAsync(team.Accountant, team.ProjectId, team.EngineerId);
        Assert.Equal(800m, account.GetProperty("custody").Dec("balance"));
        Assert.Equal(200m, account.GetProperty("custody").Dec("spentApproved"));
        Assert.Equal(["Funding", "Purchase"], account.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("type").GetString()));

        // A return by the accountant: never more than the custody holds, never by the holder, once per key.
        var tooMuch = await api.ReturnAsync(team.Accountant, team.ProjectId, team.EngineerId, 900m);
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("custody_return_above_balance", await CodeOf(tooMuch));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ReturnAsync(team.Engineer, team.ProjectId, team.EngineerId, 100m)).StatusCode);
        var returnKey = Guid.NewGuid();
        await EnsureStatus(await api.ReturnAsync(team.Accountant, team.ProjectId, team.EngineerId, 300m, returnKey), HttpStatusCode.Created);
        await EnsureStatus(await api.ReturnAsync(team.Accountant, team.ProjectId, team.EngineerId, 300m, returnKey), HttpStatusCode.OK);
        Assert.Equal(500m, await api.BalanceAsync(team.Engineer, team.ProjectId, team.EngineerId));

        // The statement runs oldest first and ends on the balance.
        var lines = (await api.CustodyAsync(team.Engineer, team.ProjectId, team.EngineerId)).GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([1_000m, -200m, -300m], lines.Select(line => line.Dec("amount")));
        Assert.Equal(500m, lines[^1].Dec("balance"));
    }

    [DatabaseFact]
    public async Task A_custody_is_seen_by_its_holder_and_by_reviewers_only()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var team = await api.TeamAsync();
        var (otherId, other) = await api.CreateSignedInUserAsync(team.Executive, "engineer");
        await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/members", team.Executive,
            new { userId = otherId, responsibility = "SiteEngineer", startDate = "2026-01-01" });
        await api.FundAsync(team.Engineer, team.Accountant, team.ProjectId, 500m);

        // Another engineer of the project sees only their own custody; the engineer's is not found for them.
        var list = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{team.ProjectId}/custody", other));
        Assert.All(list.GetProperty("items").EnumerateArray(), item => Assert.Equal(otherId, item.GetProperty("holderUserId").GetInt32()));
        Assert.False(list.GetProperty("seesAll").GetBoolean());
        var peek = await api.SendAsync(HttpMethod.Get, $"/api/projects/{team.ProjectId}/custody/{team.EngineerId}", other);
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);

        // The accountant and the project's manager see every custody of the project.
        foreach (var reviewer in new[] { team.Accountant, team.Manager })
        {
            var all = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{team.ProjectId}/custody", reviewer));
            Assert.Contains(all.GetProperty("items").EnumerateArray(), item => item.GetProperty("holderUserId").GetInt32() == team.EngineerId);
            Assert.Equal(500m, all.Dec("balance"));
        }

        // Someone off the project gets 404 for the whole project; an engineer can't fund someone else's custody.
        var (_, outsider) = await api.CreateSignedInUserAsync(team.Executive, "engineer");
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{team.ProjectId}/custody", outsider)).StatusCode);
        var forOther = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/custody/fundings", other,
            new { holderUserId = team.EngineerId, amount = 100m, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.Forbidden, forOther.StatusCode);

        // A holder must be on the project.
        var (strangerId, _) = await api.CreateSignedInUserAsync(team.Executive, "engineer");
        var stranger = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/custody/fundings", team.Executive,
            new { holderUserId = strangerId, amount = 100m, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
    }

    [DatabaseFact]
    public async Task Under_the_block_policy_a_custody_purchase_above_the_balance_is_refused_and_nothing_is_saved()
    {
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:CustodyBalanceCheck"] = "Block" });
        var team = await api.TeamAsync();
        var concrete = await api.CreateItemAsync(team.Manager, team.ProjectId, "Concrete works");

        var refused = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/expenses", team.Engineer,
            FinancialWorkspaceApi.CashPurchaseBody("Sand", 1m, 50m, concrete.Id()));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("custody_insufficient", await CodeOf(refused));

        await api.FundAsync(team.Engineer, team.Accountant, team.ProjectId, 100m);
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/expenses", team.Engineer,
            FinancialWorkspaceApi.CashPurchaseBody("Sand", 1m, 80m, concrete.Id())), HttpStatusCode.Created);
        var second = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/expenses", team.Engineer,
            FinancialWorkspaceApi.CashPurchaseBody("Gravel", 1m, 30m, concrete.Id()));
        Assert.Equal("custody_insufficient", await CodeOf(second));

        Assert.Equal(1, await api.QueryAsync(db => db.ProjectExpenses.CountAsync(expense => expense.ProjectId == team.ProjectId)));
        Assert.Equal(20m, await api.BalanceAsync(team.Engineer, team.ProjectId, team.EngineerId));
    }

    [DatabaseFact]
    public async Task A_supplier_delivers_two_materials_to_two_work_items_and_its_accounts_reconcile_across_projects()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var team = await api.TeamAsync("BDR-SUP-001");
        var supplier = await api.CreateSupplierAsync(team.Executive, "Cemex");
        var otherSupplier = await api.CreateSupplierAsync(team.Executive, "Sinai Sand");
        var concrete = await api.CreateItemAsync(team.Manager, team.ProjectId, "Concrete works");
        var masonry = await api.CreateItemAsync(team.Manager, team.ProjectId, "Masonry works");

        var cement = await api.AgreeAsync(team.Manager, team.ProjectId, supplier, concrete.Id(), "Cement 42.5", "Bag", 100m, 50m);
        Assert.Equal(5_000m, cement.Dec("agreedValue"));
        var sand = await api.AgreeAsync(team.Manager, team.ProjectId, supplier, masonry.Id(), "Sand", "M3");

        // The agreement decides the work item; another supplier or another unit under it is refused.
        var wrongSupplier = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/expenses", team.Engineer,
            SupplyBody(otherSupplier, 1m, 50m, "Credit", new() { ["supplyAgreementId"] = cement.Id() }));
        Assert.Contains("supplyAgreementId", await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(wrongSupplier));
        var wrongUnit = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/expenses", team.Engineer,
            SupplyBody(supplier, 1m, 50m, "Credit", new() { ["supplyAgreementId"] = cement.Id(), ["unit"] = "Ton" }));
        Assert.Contains("unit", await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(wrongUnit));

        var cementSupply = await api.RecordApprovedAsync(team.Engineer, team.Accountant, team.ProjectId,
            SupplyBody(supplier, 40m, 50m, "Credit", new() { ["supplyAgreementId"] = cement.Id() }));
        Assert.Equal(concrete.Id(), cementSupply.GetProperty("projectItemId").GetInt32());
        var sandSupply = await api.RecordApprovedAsync(team.Engineer, team.Accountant, team.ProjectId,
            SupplyBody(supplier, 10m, 150m, "Credit", new() { ["supplyAgreementId"] = sand.Id(), ["itemName"] = "Sand", ["unit"] = "M3" }));
        await api.RecordAsync(team.Engineer, team.ProjectId,
            SupplyBody(supplier, 10m, 50m, "Credit", new() { ["supplyAgreementId"] = cement.Id() }));

        // Paid 1,000 on the cement invoice through Finance.
        await api.PaidAsync(team.Accountant, team.ProjectId, supplier, 1_000m, (cementSupply.Id(), 1_000m));

        var view = (await api.ProjectSuppliersAsync(team.Manager, team.ProjectId)).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("supplierId").GetInt32() == supplier);
        var figures = view.GetProperty("figures");
        Assert.Equal(3_500m, figures.Dec("creditRecognized"));
        Assert.Equal(1_000m, figures.Dec("paid"));
        Assert.Equal(2_500m, figures.Dec("outstanding"));
        Assert.Equal(500m, figures.Dec("pendingReview"));
        var cementAgreement = view.GetProperty("agreements").EnumerateArray().Single(agreement => agreement.Id() == cement.Id());
        Assert.Equal(40m, cementAgreement.Dec("deliveredQuantity"));
        Assert.Equal(10m, cementAgreement.Dec("pendingQuantity"));
        Assert.Equal(60m, cementAgreement.Dec("remainingQuantity"));
        Assert.Equal(3_000m, cementAgreement.Dec("remainingValue"));
        var byItem = view.GetProperty("workItems").EnumerateArray().ToDictionary(row => row.GetProperty("projectItemId").GetInt32(), row => row.GetProperty("figures"));
        Assert.Equal(2_000m, byItem[concrete.Id()].Dec("recognized"));
        Assert.Equal(1_000m, byItem[concrete.Id()].Dec("outstanding"));
        Assert.Equal(1_500m, byItem[masonry.Id()].Dec("recognized"));
        Assert.Equal(2, view.GetProperty("materials").GetArrayLength());

        // The same supplier on a second project, which the first project's manager doesn't see.
        var (secondManagerId, secondManager) = await api.CreateSignedInUserAsync(team.Executive, "manager");
        var second = (await api.CreateProjectAsync(team.Executive, "BDR-SUP-002", secondManagerId, siteEngineers: [team.EngineerId])).Id();
        await api.RecordApprovedAsync(team.Engineer, team.Accountant, second, SupplyBody(supplier, 14m, 50m, "Credit"));

        var company = await api.SupplierAccountAsync(team.Accountant, supplier);
        Assert.Equal(3_200m, company.GetProperty("totals").Dec("outstanding"));
        Assert.Equal(company.GetProperty("totals").Dec("outstanding"),
            company.GetProperty("projects").EnumerateArray().Sum(project => project.GetProperty("figures").Dec("outstanding")));
        Assert.Equal(3_200m, company.GetProperty("ledger").EnumerateArray().First().Dec("balance"));

        var managerView = await api.SupplierAccountAsync(team.Manager, supplier);
        Assert.Equal(2_500m, managerView.GetProperty("totals").Dec("outstanding"));
        Assert.Single(managerView.GetProperty("projects").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/suppliers/{supplier}/account", team.Engineer)).StatusCode);

        // A closed agreement takes no new supply; what was delivered stays.
        var closed = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/supply-agreements/{cement.Id()}/close", team.Manager,
            new { rowVersion = cement.GetProperty("rowVersion").GetString() });
        await EnsureStatus(closed, HttpStatusCode.OK);
        var late = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/expenses", team.Engineer,
            SupplyBody(supplier, 1m, 50m, "Credit", new() { ["supplyAgreementId"] = cement.Id() }));
        Assert.Equal("supply_agreement_closed", await CodeOf(late));
    }

    [DatabaseFact]
    public async Task Supply_agreements_stay_inside_their_project_and_are_written_by_the_manager_or_purchasing()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var team = await api.TeamAsync("BDR-SUP-003");
        var supplier = await api.CreateSupplierAsync(team.Executive, "Ezz Steel");
        var concrete = await api.CreateItemAsync(team.Manager, team.ProjectId, "Concrete works");
        var other = (await api.CreateProjectAsync(team.Executive, "BDR-SUP-004", team.ManagerId)).Id();
        var foreignItem = await api.CreateItemAsync(team.Manager, other, "Foreign item");

        var crossProject = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/supply-agreements", team.Manager,
            new { supplierId = supplier, projectItemId = foreignItem.Id(), materialName = "Rebar", unit = "Ton" });
        Assert.Contains("projectItemId", await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(crossProject));

        var byEngineer = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/supply-agreements", team.Engineer,
            new { supplierId = supplier, projectItemId = concrete.Id(), materialName = "Rebar", unit = "Ton" });
        Assert.Equal(HttpStatusCode.Forbidden, byEngineer.StatusCode);

        var (_, purchasing) = await api.CreateSignedInUserAsync(team.Executive, "purchasing");
        var agreement = await api.AgreeAsync(purchasing, team.ProjectId, supplier, concrete.Id(), "Rebar", "Ton", 10m, 40_000m);
        var twice = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/supply-agreements", team.Manager,
            new { supplierId = supplier, projectItemId = concrete.Id(), materialName = "Rebar", unit = "Ton" });
        Assert.Equal("supply_agreement_exists", await CodeOf(twice));

        // The engineer sees the agreement and its quantities, never its prices.
        var listed = await api.SendAsync(HttpMethod.Get, $"/api/projects/{team.ProjectId}/supply-agreements", team.Engineer);
        await EnsureStatus(listed, HttpStatusCode.OK);
        var list = await Json(listed);
        var seen = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(agreement.Id(), seen.Id());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("agreedUnitPrice").ValueKind);
        Assert.Equal(10m, seen.Dec("agreedQuantity"));

        // Its supplier and work item never change.
        var moved = await api.SendAsync(HttpMethod.Put, $"/api/projects/{team.ProjectId}/supply-agreements/{agreement.Id()}", team.Manager, new
        {
            supplierId = supplier, projectItemId = (await api.CreateItemAsync(team.Manager, team.ProjectId, "Masonry")).Id(), materialName = "Rebar",
            unit = "Ton", rowVersion = agreement.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal("supply_agreement_parties_fixed", await CodeOf(moved));
    }

    [DatabaseFact]
    public async Task The_database_refuses_custody_rows_the_rules_never_write()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var team = await api.TeamAsync("BDR-CUS-009");

        async Task<bool> RefusedAsync(CustodyEntry entry) => await api.QueryAsync(async db =>
        {
            db.CustodyEntries.Add(entry);
            try
            {
                await db.SaveChangesAsync();
                return false;
            }
            catch (DbUpdateException)
            {
                return true;
            }
        });

        CustodyEntry Entry(CustodyEntryKind kind, CustodyEntryStatus status, decimal amount, string? sourceType = null, int? sourceId = null) => new()
        {
            ProjectId = team.ProjectId, HolderUserId = team.EngineerId, Kind = kind, Status = status, Amount = amount, EntryDate = new DateOnly(2026, 1, 5),
            Method = "Cash", SourceType = sourceType, SourceId = sourceId, CreatedAt = DateTime.UtcNow, CreatedBy = team.AccountantId,
        };

        Assert.True(await RefusedAsync(Entry(CustodyEntryKind.Funding, CustodyEntryStatus.Pending, 100m)));    // only payroll waits for review
        Assert.True(await RefusedAsync(Entry(CustodyEntryKind.Return, CustodyEntryStatus.Posted, 0m)));       // never zero
        Assert.True(await RefusedAsync(Entry(CustodyEntryKind.Funding, CustodyEntryStatus.Posted, 10m, "PaymentRequest")));  // a source names its id

        // The same paying record moves money once.
        Assert.False(await RefusedAsync(Entry(CustodyEntryKind.Payroll, CustodyEntryStatus.Pending, 10m, "PayrollSettlement", 77)));
        Assert.True(await RefusedAsync(Entry(CustodyEntryKind.Payroll, CustodyEntryStatus.Pending, 10m, "PayrollSettlement", 77)));
    }
}
