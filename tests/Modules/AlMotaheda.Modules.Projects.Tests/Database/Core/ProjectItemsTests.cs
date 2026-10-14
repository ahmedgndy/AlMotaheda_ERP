using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Core;

/// <summary>The project items table and details on SQL Server: linking, totals, the not-linked group, and the item rules.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ProjectItemsTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Linking_a_record_moves_its_amounts_into_the_item_and_out_of_the_not_linked_group_without_adding_a_row()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var concrete = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works");
        var steel = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Steel fixing", "SteelFixing");

        // Linked at recording, linked by hand later, a credit invoice paid in part, and a site expense with no supplier.
        var linked = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId,
            SupplyBody(supplier, 100m, 1_000m, "Custody", new() { ["projectItemId"] = concrete.Id() }));
        var loose = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(supplier, 50m, 1_000m, "Credit"));
        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId,
            SupplyBody(supplier, 30m, 1_000m, "Credit", new() { ["projectItemId"] = steel.Id() }));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, supplier, 10_000m, (invoice.Id(), 10_000m));
        await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, ExpenseBody(700m, "Custody"));
        await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(supplier, 999m, 999m, "Credit", new() { ["projectItemId"] = concrete.Id() }));

        var before = await api.ItemsAsync(cast.Executive, cast.ProjectId);
        var totals = before.GetProperty("totals");
        var unallocated = before.GetProperty("unallocated");
        var reconciliation = before.GetProperty("reconciliation");
        Assert.Equal(130_000m, totals.Money("supplierCost"));
        Assert.Equal(130_000m, totals.Money("totalCost"));
        Assert.Equal(110_000m, totals.Money("paid"));
        Assert.Equal(20_000m, totals.Money("outstanding"));
        Assert.Equal(2, totals.GetProperty("supplierRecordCount").GetInt32());
        Assert.Equal(1, totals.GetProperty("supplierCount").GetInt32());
        Assert.Equal(50_000m, unallocated.Money("totalCost"));
        Assert.Equal(0m, unallocated.Money("paid"));
        Assert.Equal(130_000m, reconciliation.Money("itemsTotalCost"));
        Assert.Equal(50_000m, reconciliation.Money("unallocatedTotalCost"));
        Assert.Equal(700m, reconciliation.Money("otherCosts"));
        Assert.Equal(180_700m, reconciliation.Money("projectRecognizedCost"));

        var rows = before.GetProperty("items").EnumerateArray().ToDictionary(row => row.Id());
        Assert.Equal(100_000m, rows[concrete.Id()].Money("totalCost"));
        Assert.Equal(100_000m, rows[concrete.Id()].Money("paid"));
        Assert.Equal(30_000m, rows[steel.Id()].Money("totalCost"));
        Assert.Equal(10_000m, rows[steel.Id()].Money("paid"));
        Assert.Equal(20_000m, rows[steel.Id()].Money("outstanding"));

        var group = await api.ItemDetailAsync(cast.Executive, cast.ProjectId, null);
        Assert.Equal(JsonValueKind.Null, group.GetProperty("item").ValueKind);
        Assert.Equal([loose.Id()], group.GetProperty("supplierLines").EnumerateArray().Select(line => line.GetProperty("expenseId").GetInt32()));
        Assert.Equal(50_000m, group.GetProperty("totals").Money("totalCost"));

        // The manager links the loose record to concrete.
        await EnsureStatus(await api.LinkAsync(cast.Manager, cast.ProjectId, loose, concrete.Id()), HttpStatusCode.OK);

        var after = await api.ItemsAsync(cast.Executive, cast.ProjectId);
        Assert.Equal(180_000m, after.GetProperty("totals").Money("totalCost"));
        Assert.Equal(0m, after.GetProperty("unallocated").Money("totalCost"));
        Assert.Equal(0, after.GetProperty("unallocated").GetProperty("supplierRecordCount").GetInt32());
        Assert.Equal(180_700m, after.GetProperty("reconciliation").Money("projectRecognizedCost"));
        var concreteRow = after.GetProperty("items").EnumerateArray().Single(row => row.Id() == concrete.Id());
        Assert.Equal(150_000m, concreteRow.Money("totalCost"));
        Assert.Equal(50_000m, concreteRow.Money("outstanding"));
        Assert.Equal(2, concreteRow.GetProperty("supplierRecordCount").GetInt32());

        var detail = await api.ItemDetailAsync(cast.Executive, cast.ProjectId, concrete.Id());
        Assert.Equal(150_000m, detail.GetProperty("totals").Money("totalCost"));
        Assert.Equal(detail.GetProperty("totals").Money("totalCost"), detail.GetProperty("item").Money("totalCost"));
        Assert.Equal(2, detail.GetProperty("supplierLines").GetArrayLength());
        Assert.Single(detail.GetProperty("pendingSupplierLines").EnumerateArray());
        // Five records were recorded; linking created none.
        Assert.Equal(5, await api.QueryAsync(db => db.ProjectExpenses.CountAsync()));

        // Linking to a rejected record, or to an item of another project, is refused; the link can also be removed.
        var otherProject = await api.CreateProjectAsync(cast.Executive, "OTHER-1", cast.Executive.UserId);
        var foreignItem = await api.CreateItemAsync(cast.Executive, otherProject.Id(), "Foreign");
        var current = await api.GetExpenseAsync(cast.Executive, cast.ProjectId, loose.Id());
        var crossProject = await api.LinkAsync(cast.Manager, cast.ProjectId, current, foreignItem.Id());
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);
        await EnsureStatus(await api.LinkAsync(cast.Accountant, cast.ProjectId, current, null), HttpStatusCode.OK);
        Assert.Equal(50_000m, (await api.ItemsAsync(cast.Executive, cast.ProjectId)).GetProperty("unallocated").Money("totalCost"));
    }

    [DatabaseFact]
    public async Task Items_are_managed_by_the_manager_or_an_executive_with_unique_names_and_a_delete_only_while_empty()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        var byEngineer = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items", cast.Engineer,
            new { nameAr = "أ", nameEn = "A", category = "Concrete", unit = "M3" });
        Assert.Equal(HttpStatusCode.Forbidden, byEngineer.StatusCode);

        var item = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works");
        var sameName = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items", cast.Manager,
            new { nameAr = "آخر", nameEn = "concrete works", category = "Concrete", unit = "M3" });
        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
        Assert.Equal("item_name_taken", await CodeOf(sameName));

        var renamed = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/items/{item.Id()}", cast.Executive,
            new { nameAr = "الخرسانة", nameEn = "Concrete", category = "Concrete", unit = "M3", notes = "Phase 1", rowVersion = item.GetProperty("rowVersion").GetString() });
        await EnsureStatus(renamed, HttpStatusCode.OK);
        var renamedItem = await Json(renamed);
        Assert.Equal("Concrete", renamedItem.GetProperty("nameEn").GetString());

        // A record is linked: the item can't be deleted any more, only deactivated.
        var record = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(supplier, 1m, 10m, "Custody", new() { ["projectItemId"] = item.Id() }));
        var delete = await api.SendAsync(HttpMethod.Delete, $"/api/projects/{cast.ProjectId}/items/{item.Id()}", cast.Manager);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("item_has_records", await CodeOf(delete));

        var deactivated = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items/{item.Id()}/deactivate", cast.Manager,
            new { rowVersion = renamedItem.GetProperty("rowVersion").GetString() });
        await EnsureStatus(deactivated, HttpStatusCode.OK);
        var inactive = await Json(deactivated);
        Assert.Equal("Inactive", inactive.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, inactive.GetProperty("deactivatedAt").ValueKind);

        // Nothing new can be linked to it, but its record stays and still counts once approved.
        var newLink = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer,
            SupplyBody(supplier, 1m, 10m, "Custody", new() { ["projectItemId"] = item.Id() }));
        Assert.Equal(HttpStatusCode.Conflict, newLink.StatusCode);
        Assert.Equal("item_inactive", await CodeOf(newLink));
        await api.ApproveAsync(cast.Accountant, cast.ProjectId, record);
        var active = await api.ItemsAsync(cast.Executive, cast.ProjectId);
        Assert.Empty(active.GetProperty("items").EnumerateArray());
        Assert.Equal(10m, active.GetProperty("totals").Money("totalCost"));
        var all = await api.ItemsAsync(cast.Executive, cast.ProjectId, "?includeInactive=true");
        Assert.Single(all.GetProperty("items").EnumerateArray());

        var twice = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items/{item.Id()}/deactivate", cast.Manager,
            new { rowVersion = inactive.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(twice));

        var reactivated = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items/{item.Id()}/reactivate", cast.Executive,
            new { rowVersion = inactive.GetProperty("rowVersion").GetString() });
        await EnsureStatus(reactivated, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, (await Json(reactivated)).GetProperty("deactivatedAt").ValueKind);

        // An empty item can be deleted.
        var empty = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Mistake");
        var removed = await api.SendAsync(HttpMethod.Delete, $"/api/projects/{cast.ProjectId}/items/{empty.Id()}", cast.Manager);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var gone = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/items/{empty.Id()}", cast.Executive);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [DatabaseFact]
    public async Task Records_of_one_project_never_show_on_another_and_an_outsider_gets_404_everywhere()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("PROJ-A");
        var (otherManagerId, otherManager) = await api.CreateSignedInUserAsync(cast.Executive, "manager");
        var projectB = (await api.CreateProjectAsync(cast.Executive, "PROJ-B", otherManagerId)).Id();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        var item = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works");
        var approved = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(supplier, 10m, 100m, "Credit"));
        var payment = await api.PaidAsync(cast.Accountant, cast.ProjectId, supplier, 400m, (approved.Id(), 400m));

        // Project B is empty, whatever project A holds.
        Assert.Empty((await api.ListExpensesAsync(cast.Executive, projectB)).GetProperty("items").EnumerateArray());
        Assert.Equal(0m, (await api.ItemsAsync(cast.Executive, projectB)).GetProperty("reconciliation").Money("projectRecognizedCost"));
        Assert.Empty((await api.BalancesAsync(cast.Executive, projectB)).GetProperty("items").EnumerateArray());
        var paymentsB = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectB}/supplier-payments", cast.Executive));
        Assert.Equal(0, paymentsB.GetProperty("totalCount").GetInt32());

        // Project A's ids don't resolve under project B.
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectB}/expenses/{approved.Id()}", cast.Executive)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectB}/items/{item.Id()}", cast.Executive)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.VoidAsync(cast.Executive, projectB, payment)).StatusCode);

        // The other manager is not on project A: 404 on every endpoint, not 403.
        string[] reads = [$"/api/projects/{cast.ProjectId}/items", $"/api/projects/{cast.ProjectId}/items/unallocated",
            $"/api/projects/{cast.ProjectId}/expenses", $"/api/projects/{cast.ProjectId}/supplier-payments", $"/api/projects/{cast.ProjectId}/supplier-balances"];
        foreach (var path in reads)
        {
            var response = await api.SendAsync(HttpMethod.Get, path, otherManager);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", await CodeOf(response));
        }

        var createItem = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items", otherManager,
            new { nameAr = "أ", nameEn = "A", category = "Concrete", unit = "M3" });
        Assert.Equal(HttpStatusCode.NotFound, createItem.StatusCode);
        var record = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", otherManager, ExpenseBody(10m, "Custody"));
        Assert.Equal(HttpStatusCode.NotFound, record.StatusCode);
        var link = await api.LinkAsync(otherManager, cast.ProjectId, approved, item.Id());
        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
    }
}
