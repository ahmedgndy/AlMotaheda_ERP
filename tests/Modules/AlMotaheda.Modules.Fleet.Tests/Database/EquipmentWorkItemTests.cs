using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Fleet.Tests.Database;

/// <summary>
/// Machinery on the work items, on SQL Server: an equipment cost linked to a sub-item is a cost of that sub-item,
/// rolls up to its main item, and is counted once in the project, never again in the other costs.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class EquipmentWorkItemTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_rental_on_the_excavation_sub_item_costs_that_sub_item_and_earthworks_once_and_the_project_total_holds()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var earthworks = await CreateItemAsync(api, cast.Manager, cast.ProjectA, "Earthworks", parentItemId: null);
        var excavation = await CreateItemAsync(api, cast.Manager, cast.ProjectA, "Excavation", parentItemId: earthworks);
        var machine = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("ex-311"));
        await api.AssignedAsync(cast.Manager, cast.ProjectA, machine.Id(), "2026-01-05");
        var lessor = await api.CreateSupplierAsync(cast.Executive, "Delta Equipment Rental");

        // Fuel for the whole site (no work item): 182.5 L × 15.25 = 2,783.13, paid from custody once approved.
        var fuel = await api.RecordedCostAsync(cast.Manager, cast.ProjectA, FleetApi.FuelBody(machine.Id(), 182.5m, 15.25m));
        Assert.Equal(JsonValueKind.Null, fuel.GetProperty("projectItemId").ValueKind);
        await api.ReviewedAsync(cast.Accountant, cast.ProjectA, fuel, "approve");
        var siteOnly = await ItemsAsync(api, cast.Executive, cast.ProjectA);
        Assert.Equal(0m, siteOnly.GetProperty("totals").Money("totalCost"));
        Assert.Equal(2_783.13m, siteOnly.GetProperty("reconciliation").Money("otherCosts"));
        Assert.Equal(2_783.13m, siteOnly.GetProperty("reconciliation").Money("projectRecognizedCost"));

        // The rental on the sub-item: 15 days × 3,500 = 52,500 on credit. Recorded, it is not a cost of anything yet.
        var rental = await api.RecordedCostAsync(cast.Manager, cast.ProjectA, WithItem(FleetApi.RentalCostBody(machine.Id(), lessor, 15m, 3_500m), excavation));
        Assert.Equal(excavation, rental.GetProperty("projectItemId").GetInt32());
        Assert.Equal(0m, (await RowAsync(api, cast.Executive, cast.ProjectA, excavation)).Money("totalCost"));

        // Approved by someone else: a cost of the sub-item, and of its main item through the roll-up.
        rental = await api.ReviewedAsync(cast.Accountant, cast.ProjectA, rental, "approve");
        var items = await ItemsAsync(api, cast.Executive, cast.ProjectA);
        var rows = items.GetProperty("items").EnumerateArray().ToDictionary(row => row.Id());

        var sub = rows[excavation];
        Assert.Equal(52_500m, sub.Money("equipmentCost"));
        Assert.Equal(52_500m, sub.Money("totalCost"));
        Assert.Equal(0m, sub.Money("paid"));
        Assert.Equal(52_500m, sub.Money("equipmentOutstanding"));
        Assert.Equal(1, sub.GetProperty("equipmentRecordCount").GetInt32());
        Assert.Equal(1, sub.GetProperty("equipmentCount").GetInt32());

        var main = rows[earthworks];
        Assert.Equal(0m, main.Money("totalCost"));
        Assert.Equal(52_500m, main.GetProperty("withSubItems").Money("equipmentCost"));
        Assert.Equal(52_500m, main.GetProperty("withSubItems").Money("totalCost"));

        // Counted once: in the items total, out of the other costs; the project total is the Equipment group.
        var reconciliation = items.GetProperty("reconciliation");
        Assert.Equal(52_500m, items.GetProperty("totals").Money("equipmentCost"));
        Assert.Equal(52_500m, reconciliation.Money("itemsTotalCost"));
        Assert.Equal(0m, reconciliation.Money("unallocatedTotalCost"));
        Assert.Equal(2_783.13m, reconciliation.Money("otherCosts"));
        Assert.Equal(55_283.13m, reconciliation.Money("projectRecognizedCost"));

        var overview = await GetAsync(api, cast.Executive, $"/api/projects/{cast.ProjectA}/overview");
        var equipmentGroup = overview.GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("costGroup").GetString() == "Equipment");
        Assert.Equal(55_283.13m, equipmentGroup.Money("recognized"));
        Assert.Equal(reconciliation.Money("projectRecognizedCost"), overview.GetProperty("totals").Money("recognized"));
        Assert.Equal(52_500m, overview.GetProperty("items").GetProperty("totals").Money("totalCost"));
        Assert.Equal(0m, overview.GetProperty("items").GetProperty("notLinked").Money("totalCost"));

        // The main item's breakdown lists the rental once, on the sub-item, with the machine as the party.
        var breakdown = await GetAsync(api, cast.Executive, $"/api/projects/{cast.ProjectA}/items/{earthworks}/cost-breakdown");
        Assert.Equal(52_500m, breakdown.GetProperty("totals").Money("totalCost"));
        var line = Assert.Single(breakdown.GetProperty("transactions").EnumerateArray());
        Assert.Equal("Equipment", line.GetProperty("type").GetString());
        Assert.Equal("Rental", line.GetProperty("recordKind").GetString());
        Assert.Equal(rental.Id(), line.GetProperty("recordId").GetInt32());
        Assert.Equal(excavation, line.GetProperty("itemId").GetInt32());
        Assert.Equal(machine.Id(), line.GetProperty("partyId").GetInt32());
        Assert.Equal(52_500m, line.Money("amount"));
        var machineSummary = Assert.Single(breakdown.GetProperty("machines").EnumerateArray());
        Assert.Equal(52_500m, machineSummary.Money("outstanding"));
        Assert.Empty(breakdown.GetProperty("suppliers").EnumerateArray());
        var filtered = await GetAsync(api, cast.Executive,
            $"/api/projects/{cast.ProjectA}/items/{earthworks}/cost-breakdown?type=Equipment&equipmentId={machine.Id()}&subItemId={excavation}");
        Assert.Equal(52_500m, filtered.GetProperty("filtered").Money("amount"));

        // The sub-item's detail lists it; the not-linked group never holds a site cost.
        var detail = await GetAsync(api, cast.Executive, $"/api/projects/{cast.ProjectA}/items/{excavation}");
        Assert.Equal(rental.Id(), Assert.Single(detail.GetProperty("equipmentLines").EnumerateArray()).GetProperty("costId").GetInt32());
        var notLinked = await GetAsync(api, cast.Executive, $"/api/projects/{cast.ProjectA}/items/unallocated");
        Assert.Empty(notLinked.GetProperty("equipmentLines").EnumerateArray());

        // Paying the rental settles the debt; it is never a second cost.
        await api.PaidCostAsync(cast.Accountant, cast.ProjectA, rental);
        var paid = await ItemsAsync(api, cast.Executive, cast.ProjectA);
        var paidRow = paid.GetProperty("items").EnumerateArray().Single(row => row.Id() == excavation);
        Assert.Equal(52_500m, paidRow.Money("totalCost"));
        Assert.Equal(52_500m, paidRow.Money("equipmentPaid"));
        Assert.Equal(0m, paidRow.Money("outstanding"));
        Assert.Equal(55_283.13m, paid.GetProperty("reconciliation").Money("projectRecognizedCost"));

        // A work item with an equipment cost can't be deleted.
        var delete = await api.SendAsync(HttpMethod.Delete, $"/api/projects/{cast.ProjectA}/items/{excavation}", cast.Manager);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("item_has_records", await CodeOf(delete));
    }

    [DatabaseFact]
    public async Task A_cost_is_linked_only_to_an_active_work_item_of_its_own_project()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var machine = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("ex-312"));
        await api.AssignedAsync(cast.Manager, cast.ProjectA, machine.Id(), "2026-01-05");
        var otherProjectItem = await CreateItemAsync(api, cast.OtherManager, cast.ProjectB, "Foundations", parentItemId: null);

        // An item of another project is "not found" on this one: a form error on its own field, nothing saved.
        var foreign = await api.RecordCostAsync(cast.Manager, cast.ProjectA, WithItem(FleetApi.FuelBody(machine.Id(), 10m, 15m), otherProjectItem));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(["projectItemId"], (await Json(foreign)).GetProperty("errors").EnumerateObject().Select(field => field.Name));

        // An inactive item takes no new cost.
        var oldWorks = await CreateItemAsync(api, cast.Manager, cast.ProjectA, "Old works", parentItemId: null);
        await DeactivateAsync(api, cast.Manager, cast.ProjectA, oldWorks);
        var inactive = await api.RecordCostAsync(cast.Manager, cast.ProjectA, WithItem(FleetApi.FuelBody(machine.Id(), 10m, 15m), oldWorks));
        Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
        Assert.Equal("item_inactive", await CodeOf(inactive));
        Assert.Empty((await api.ListCostsAsync(cast.Executive, cast.ProjectA)).GetProperty("items").EnumerateArray());

        // A cost recorded on an active item keeps it when its recorder fixes it after the item is deactivated.
        var active = await CreateItemAsync(api, cast.Manager, cast.ProjectA, "Grading", parentItemId: null);
        var cost = await api.RecordedCostAsync(cast.Manager, cast.ProjectA, WithItem(FleetApi.FuelBody(machine.Id(), 10m, 15m), active));
        var returned = await api.ReviewedAsync(cast.Accountant, cast.ProjectA, cost, "return", "Attach the fuel receipt");
        await DeactivateAsync(api, cast.Manager, cast.ProjectA, active);
        var fixedBody = WithItem(FleetApi.FuelBody(machine.Id(), 12m, 15m), active);
        fixedBody["rowVersion"] = returned.GetProperty("rowVersion").GetString();
        var resubmitted = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectA}/equipment-costs/{cost.Id()}", cast.Manager, fixedBody);
        await EnsureStatus(resubmitted, HttpStatusCode.OK);
        Assert.Equal(active, (await Json(resubmitted)).GetProperty("projectItemId").GetInt32());
    }

    private static Dictionary<string, object?> WithItem(object body, int projectItemId)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(body))!;
        fields["projectItemId"] = projectItemId;
        return fields;
    }

    private static async Task<int> CreateItemAsync(ProjectsApi api, Session session, int projectId, string nameEn, int? parentItemId)
    {
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/items", session,
            new { nameAr = $"بند {nameEn}", nameEn, category = "Concrete", unit = "M3", parentItemId });
        await EnsureStatus(response, HttpStatusCode.Created);
        return (await Json(response)).Id();
    }

    private static async Task DeactivateAsync(ProjectsApi api, Session session, int projectId, int itemId)
    {
        var item = await GetAsync(api, session, $"/api/projects/{projectId}/items/{itemId}");
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/items/{itemId}/deactivate", session,
            new { rowVersion = item.GetProperty("item").GetProperty("rowVersion").GetString() });
        await EnsureStatus(response, HttpStatusCode.OK);
    }

    private static Task<JsonElement> ItemsAsync(ProjectsApi api, Session session, int projectId) =>
        GetAsync(api, session, $"/api/projects/{projectId}/items?includeInactive=true");

    private static async Task<JsonElement> RowAsync(ProjectsApi api, Session session, int projectId, int itemId) =>
        (await ItemsAsync(api, session, projectId)).GetProperty("items").EnumerateArray().Single(row => row.Id() == itemId);

    private static async Task<JsonElement> GetAsync(ProjectsApi api, Session session, string path)
    {
        var response = await api.SendAsync(HttpMethod.Get, path, session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }
}
