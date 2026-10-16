using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Fleet.Tests.Database.FleetApi;

namespace AlMotaheda.Modules.Fleet.Tests.Database;

/// <summary>
/// Correcting an approved equipment cost (refactor plan M6): decided by an Executive; a credit cost only before its
/// payment is requested, and the payment request then asks for the corrected amount.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class EquipmentCostAdjustmentTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_corrected_rental_is_requested_and_paid_at_its_corrected_amount_and_never_corrected_while_being_paid()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var lessor = await api.CreateSupplierAsync(cast.Executive, "Delta Equipment Rental");
        var machine = await api.RegisterAsync(cast.Executive, EquipmentBody("EX-210", "Rented",
            new { supplierId = lessor, rateBasis = "Day", rate = 3_500m, startDate = "2026-01-01" }));
        await api.AssignedAsync(cast.Manager, cast.ProjectA, machine.Id(), "2026-01-01");
        var rental = await api.RecordedCostAsync(cast.Manager, cast.ProjectA, RentalCostBody(machine.Id(), lessor, 15m, 3_500m));
        var approved = await api.ReviewedAsync(cast.Accountant, cast.ProjectA, rental, "approve");

        // The lessor billed one day too many: 3,500 less, asked by the manager, decided by the Executive.
        var asked = await AdjustAsync(api, cast.Manager, cast.ProjectA, approved.Id(), -3_500m);
        await EnsureStatus(asked, HttpStatusCode.Created);
        var adjustment = Assert.Single((await Json(asked)).GetProperty("adjustments").EnumerateArray());

        // Its payment waits for the decision.
        var early = await api.PayAsync(cast.Accountant, cast.ProjectA, await Json(asked));
        Assert.Equal("adjustment_open", await CodeOf(early));

        var decided = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectA}/equipment-cost-adjustments/{adjustment.Id()}/approve",
            cast.Executive, new { rowVersion = adjustment.GetProperty("rowVersion").GetString() });
        await EnsureStatus(decided, HttpStatusCode.OK);
        var corrected = await Json(decided);
        Assert.Equal((52_500m, 49_000m, 49_000m), (corrected.Money("amount"), corrected.Money("adjustedAmount"), corrected.Money("outstanding")));
        Assert.Equal(49_000m, (await api.SummaryAsync(cast.Accountant, cast.ProjectA)).GetProperty("toDate").Money("total"));

        // The payment request asks for the corrected amount; while it waits, no new correction.
        var requested = await api.PayAsync(cast.Accountant, cast.ProjectA, corrected);
        await EnsureStatus(requested, HttpStatusCode.Created);
        var request = await Json(requested);
        Assert.Equal(49_000m, request.Money("amount"));
        Assert.Equal("cost_being_paid", await CodeOf(await AdjustAsync(api, cast.Manager, cast.ProjectA, approved.Id(), -100m)));

        // Paid at 49,000.
        await api.SettleAsync(cast.Accountant, request);
        var paid = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectA}/equipment-costs/{approved.Id()}", cast.Accountant));
        Assert.Equal((49_000m, 0m, "Paid"), (paid.Money("paid"), paid.Money("outstanding"), paid.GetProperty("paymentState").GetString()));
    }

    private static Task<HttpResponseMessage> AdjustAsync(ProjectsApi api, Session session, int projectId, int costId, decimal amount) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/equipment-costs/{costId}/adjustments", session,
            new { amount, reason = "The lessor billed one day too many" });
}
