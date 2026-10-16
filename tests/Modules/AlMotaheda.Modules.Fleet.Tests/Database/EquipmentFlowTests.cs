using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Fleet.Tests.Database;

/// <summary>
/// The equipment register, assignments over time and equipment costs end to end on SQL Server, signed in as the
/// default roles.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class EquipmentFlowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_machine_moves_between_projects_without_a_second_register_row_and_is_on_one_project_at_a_time()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var machine = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("ex-104"));
        Assert.Equal("EX-104", machine.GetProperty("assetNo").GetString());
        Assert.True(machine.GetProperty("isAvailable").GetBoolean());

        var duplicate = await api.SendAsync(HttpMethod.Post, "/api/equipment", cast.Executive, FleetApi.EquipmentBody("EX-104"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("asset_no_taken", await CodeOf(duplicate));

        var onA = await api.AssignedAsync(cast.Manager, cast.ProjectA, machine.Id(), "2026-01-05");
        Assert.True(onA.GetProperty("isCurrent").GetBoolean());

        // A second project can't take it while it is on the first one.
        var toB = await api.AssignAsync(cast.OtherManager, cast.ProjectB, machine.Id(), "2026-01-10");
        Assert.Equal(HttpStatusCode.Conflict, toB.StatusCode);
        Assert.Equal("equipment_assigned", await CodeOf(toB));
        var twiceOnA = await api.AssignAsync(cast.Manager, cast.ProjectA, machine.Id(), "2026-01-10");
        Assert.Equal(HttpStatusCode.Conflict, twiceOnA.StatusCode);

        var ended = await api.EndAsync(cast.Manager, cast.ProjectA, onA, "2026-02-28");
        Assert.False(ended.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("2026-02-28", ended.GetProperty("endDate").GetString());

        // Its stays never overlap: the next one starts on or after the day the last one ended.
        var overlapping = await api.AssignAsync(cast.OtherManager, cast.ProjectB, machine.Id(), "2026-02-20");
        Assert.Equal(HttpStatusCode.BadRequest, overlapping.StatusCode);
        await api.AssignedAsync(cast.OtherManager, cast.ProjectB, machine.Id(), "2026-02-28");

        // Ending keeps the history on project A.
        Assert.Empty(await api.ProjectEquipmentAsync(cast.Manager, cast.ProjectA));
        var historyOfA = Assert.Single(await api.ProjectEquipmentAsync(cast.Manager, cast.ProjectA, includeEnded: true));
        Assert.Equal(onA.Id(), historyOfA.Id());
        Assert.Single(await api.ProjectEquipmentAsync(cast.OtherManager, cast.ProjectB));

        // One register row; the Executive sees both stays, project A's manager only the one on A.
        Assert.Equal(1, await api.FleetQueryAsync(db => db.Equipment.CountAsync()));
        Assert.Equal(2, await api.FleetQueryAsync(db => db.EquipmentAssignments.CountAsync(row => row.EquipmentId == machine.Id())));
        var seenByExecutive = await api.GetEquipmentAsync(cast.Executive, machine.Id());
        Assert.Equal(2, seenByExecutive.GetProperty("history").GetArrayLength());
        Assert.Equal(cast.ProjectB, seenByExecutive.GetProperty("currentAssignment").GetProperty("projectId").GetInt32());
        Assert.False(seenByExecutive.GetProperty("isAvailable").GetBoolean());
        var seenByManager = await api.GetEquipmentAsync(cast.Manager, machine.Id());
        Assert.Equal(cast.ProjectA, Assert.Single(seenByManager.GetProperty("history").EnumerateArray()).GetProperty("projectId").GetInt32());
        Assert.Equal(1, seenByManager.GetProperty("hiddenHistoryCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, seenByManager.GetProperty("currentAssignment").ValueKind);
    }

    [DatabaseFact]
    public async Task Costs_belong_to_the_project_they_were_recorded_on_and_never_show_in_another()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var machine = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("GEN-07"));
        var onA = await api.AssignedAsync(cast.Manager, cast.ProjectA, machine.Id(), "2026-01-05");

        // Never on B: no cost there.
        var neverOnB = await api.RecordCostAsync(cast.OtherManager, cast.ProjectB, FleetApi.FuelBody(machine.Id(), 100m, 15m));
        Assert.Equal(HttpStatusCode.Conflict, neverOnB.StatusCode);
        Assert.Equal("never_on_project", await CodeOf(neverOnB));

        // 182.5 L × 15.25 = 2,783.125 → 2,783.13.
        var fuel = await api.RecordedCostAsync(cast.Manager, cast.ProjectA, FleetApi.FuelBody(machine.Id(), 182.5m, 15.25m));
        Assert.Equal(2_783.13m, fuel.Money("amount"));
        Assert.Equal("Recorded", fuel.GetProperty("status").GetString());

        // The machine moves to B; the cost stays on A, and A can still record costs of its stay.
        await api.EndAsync(cast.Manager, cast.ProjectA, onA, "2026-01-31");
        await api.AssignedAsync(cast.OtherManager, cast.ProjectB, machine.Id(), "2026-02-01");
        await api.RecordedCostAsync(cast.Manager, cast.ProjectA, FleetApi.LumpBody(machine.Id(), "Transport", 1_500m));
        await api.RecordedCostAsync(cast.OtherManager, cast.ProjectB, FleetApi.FuelBody(machine.Id(), 40m, 15.25m, "2026-02-03"));

        var costsOfA = await api.ListCostsAsync(cast.Executive, cast.ProjectA);
        var costsOfB = await api.ListCostsAsync(cast.Executive, cast.ProjectB);
        Assert.Equal(2, costsOfA.GetProperty("totalCount").GetInt32());
        Assert.All(costsOfA.GetProperty("items").EnumerateArray(), cost => Assert.Equal(cast.ProjectA, cost.GetProperty("projectId").GetInt32()));
        Assert.Equal(1, costsOfB.GetProperty("totalCount").GetInt32());
        Assert.Equal(610m, Assert.Single(costsOfB.GetProperty("items").EnumerateArray()).Money("amount"));

        // A cost of A asked through B is not found, and B's manager can't read A at all.
        var throughB = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectB}/equipment-costs/{fuel.Id()}", cast.Executive);
        Assert.Equal(HttpStatusCode.NotFound, throughB.StatusCode);
        var otherManagerOnA = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectA}/equipment-costs", cast.OtherManager);
        Assert.Equal(HttpStatusCode.NotFound, otherManagerOnA.StatusCode);
        var approveThroughB = await api.ReviewAsync(cast.Accountant, cast.ProjectB, fuel, "approve");
        Assert.Equal(HttpStatusCode.NotFound, approveThroughB.StatusCode);
    }

    [DatabaseFact]
    public async Task Only_approved_costs_are_recognized_custody_is_paid_at_approval_and_credit_only_after_its_payment()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var lessor = await api.CreateSupplierAsync(cast.Executive, "Delta Equipment Rental");
        var machine = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("EX-210", "Rented",
            new { supplierId = lessor, rateBasis = "Day", rate = 3_500m, startDate = "2026-01-01" }));
        Assert.Equal(3_500m, machine.GetProperty("currentRental").Money("rate"));
        await api.AssignedAsync(cast.Manager, cast.ProjectA, machine.Id(), "2026-01-01");

        // 15 days × 3,500 = 52,500 on credit; fuel on custody; a repair on custody.
        var rental = await api.RecordedCostAsync(cast.Manager, cast.ProjectA, FleetApi.RentalCostBody(machine.Id(), lessor, 15m, 3_500m));
        var fuel = await api.RecordedCostAsync(cast.Manager, cast.ProjectA, FleetApi.FuelBody(machine.Id(), 182.5m, 15.25m));
        var repair = await api.RecordedCostAsync(cast.Manager, cast.ProjectA, FleetApi.LumpBody(machine.Id(), "Repair", 1_200m));
        Assert.Equal(52_500m, rental.Money("amount"));

        var pending = await api.SummaryAsync(cast.Accountant, cast.ProjectA);
        Assert.Equal(0m, pending.GetProperty("toDate").Money("total"));
        Assert.Equal(3, pending.GetProperty("pendingReviewCount").GetInt32());
        Assert.Equal(56_483.13m, pending.Money("pendingReviewAmount"));

        // The recorder never reviews their own cost.
        var own = await api.ReviewAsync(cast.Manager, cast.ProjectA, fuel, "approve");
        Assert.Equal(HttpStatusCode.Conflict, own.StatusCode);
        Assert.Equal("own_record", await CodeOf(own));

        var approvedFuel = await api.ReviewedAsync(cast.Accountant, cast.ProjectA, fuel, "approve");
        var approvedRental = await api.ReviewedAsync(cast.Accountant, cast.ProjectA, rental, "approve");
        var noNote = await api.ReviewAsync(cast.Accountant, cast.ProjectA, repair, "return");
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        await api.ReviewedAsync(cast.Accountant, cast.ProjectA, repair, "return", "Attach the workshop invoice");

        Assert.Equal(2_783.13m, approvedFuel.Money("paid"));
        Assert.Equal("Paid", approvedFuel.GetProperty("paymentState").GetString());
        Assert.Equal(0m, approvedRental.Money("paid"));
        Assert.Equal(52_500m, approvedRental.Money("outstanding"));
        Assert.Equal("Unpaid", approvedRental.GetProperty("paymentState").GetString());

        // An approved cost is frozen.
        var edit = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectA}/equipment-costs/{approvedFuel.Id()}", cast.Manager,
            new { equipmentId = machine.Id(), costDate = "2026-01-15", type = "Fuel", liters = 1m, pricePerLiter = 15m, channel = "Custody",
                rowVersion = approvedFuel.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(edit));

        // Custody takes no payment; credit takes exactly one.
        var payCustody = await api.PayAsync(cast.Accountant, cast.ProjectA, approvedFuel);
        Assert.Equal(HttpStatusCode.Conflict, payCustody.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(payCustody));
        var managerPays = await api.PayAsync(cast.Manager, cast.ProjectA, approvedRental);
        Assert.Equal(HttpStatusCode.Forbidden, managerPays.StatusCode);

        // Requested first: not paid yet. The same form sent again is the same request; changed, it is refused.
        var requested = await api.PayAsync(cast.Accountant, cast.ProjectA, approvedRental);
        await EnsureStatus(requested, HttpStatusCode.Created);
        var request = await Json(requested);
        Assert.Equal(52_500m, request.Money("amount"));
        var current = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectA}/equipment-costs/{approvedRental.Id()}", cast.Accountant));
        Assert.Equal(0m, current.Money("paid"));
        var resent = await api.PayAsync(cast.Accountant, cast.ProjectA, current);
        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        Assert.Equal(request.Id(), (await Json(resent)).Id());
        var changed = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectA}/equipment-costs/{approvedRental.Id()}/payment", cast.Accountant,
            new { paidOn = "2026-02-05", method = "Cash", rowVersion = current.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("idempotency_key_reused", await CodeOf(changed));

        // Approved by the Executive, sent to the bank and confirmed: now it is paid.
        await api.SettleAsync(cast.Accountant, request);
        var paid = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectA}/equipment-costs/{approvedRental.Id()}", cast.Accountant));
        Assert.Equal(52_500m, paid.Money("paid"));
        Assert.Equal(0m, paid.Money("outstanding"));
        Assert.Equal("Paid", paid.GetProperty("paymentState").GetString());
        var again = await api.PayAsync(cast.Accountant, cast.ProjectA, paid);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("already_paid", await CodeOf(again));

        // Only payments Approve (the Executive by default) removes a wrong payment.
        var accountantVoids = await api.VoidPaymentAsync(cast.Accountant, cast.ProjectA, paid);
        Assert.Equal(HttpStatusCode.Forbidden, accountantVoids.StatusCode);
        var voidedResponse = await api.VoidPaymentAsync(cast.Executive, cast.ProjectA, paid);
        await EnsureStatus(voidedResponse, HttpStatusCode.OK);
        var voided = await Json(voidedResponse);
        Assert.Equal(0m, voided.Money("paid"));
        Assert.Equal(JsonValueKind.Null, voided.GetProperty("payment").ValueKind);
        Assert.Equal("Paid from the wrong account", voided.GetProperty("lastVoidedPayment").GetProperty("reason").GetString());
        var voidAgain = await api.VoidPaymentAsync(cast.Executive, cast.ProjectA, voided);
        Assert.Equal(HttpStatusCode.Conflict, voidAgain.StatusCode);
        Assert.Equal("not_paid", await CodeOf(voidAgain));

        // The cost is owed again, so the right payment can be recorded; a second void never overwrites the first:
        // every voided payment is kept whole, with who voided it and why (payments are never edited or deleted).
        var repaid = await api.PaidCostAsync(cast.Accountant, cast.ProjectA, voided);
        Assert.Equal(52_500m, repaid.Money("paid"));
        await EnsureStatus(await api.VoidPaymentAsync(cast.Executive, cast.ProjectA, repaid), HttpStatusCode.OK);
        var history = await api.FleetQueryAsync(db => db.VoidedEquipmentCostPayments
            .Where(payment => payment.CostId == approvedRental.Id())
            .OrderBy(payment => payment.Id)
            .ToListAsync());
        Assert.Equal(2, history.Count);
        Assert.All(history, payment =>
        {
            Assert.Equal(new DateOnly(2026, 2, 5), payment.PaidOn);
            Assert.Equal("TRF-2201", payment.PaymentReference);
            Assert.Equal("Paid from the wrong account", payment.VoidReason);
        });
        Assert.True(history[0].VoidedAt <= history[1].VoidedAt);

        // The same history through the API, newest void first; another project's id finds nothing (404).
        var historyPath = $"/api/projects/{cast.ProjectA}/equipment-costs/{approvedRental.Id()}/voided-payments";
        var apiHistoryResponse = await api.SendAsync(HttpMethod.Get, historyPath, cast.Accountant);
        await EnsureStatus(apiHistoryResponse, HttpStatusCode.OK);
        var apiHistory = (await Json(apiHistoryResponse)).EnumerateArray().ToList();
        Assert.Equal([history[1].Id, history[0].Id], apiHistory.Select(row => row.GetProperty("id").GetInt32()));
        Assert.All(apiHistory, row =>
        {
            Assert.Equal("2026-02-05", row.GetProperty("paidOn").GetString());
            Assert.Equal("TRF-2201", row.GetProperty("reference").GetString());
            Assert.Equal("Paid from the wrong account", row.GetProperty("reason").GetString());
            Assert.True(row.GetProperty("voidedBy").GetProperty("userId").GetInt32() > 0);
        });
        var fromOtherProject = await api.SendAsync(
            HttpMethod.Get, $"/api/projects/{cast.ProjectB}/equipment-costs/{approvedRental.Id()}/voided-payments", cast.Executive);
        Assert.Equal(HttpStatusCode.NotFound, fromOtherProject.StatusCode);

        // The equipment group as the Projects module sees it: approved only; the returned repair is in no total.
        var figures = await api.WithServicesAsync(services => services.GetServices<IProjectCostSource>()
            .Single(source => source.Group == CostGroups.Equipment)
            .SummarizeAsync(cast.ProjectA, null, null, CancellationToken.None));
        Assert.Equal(55_283.13m, figures.Recognized);
        Assert.Equal(2_783.13m, figures.Paid);
        Assert.Equal(2, figures.RecognizedRecords);
        Assert.Equal(0m, figures.PendingReview);
        Assert.Equal(0, figures.PendingRecords);
        Assert.Equal(0m, figures.Committed);

        var february = await api.WithServicesAsync(services => services.GetServices<IProjectCostSource>()
            .Single(source => source.Group == CostGroups.Equipment)
            .SummarizeAsync(cast.ProjectA, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28), CancellationToken.None));
        Assert.Equal(0m, february.Recognized);

        var summary = await api.SummaryAsync(cast.Accountant, cast.ProjectA);
        Assert.Equal(52_500m, summary.GetProperty("toDate").Money("rental"));
        Assert.Equal(2_783.13m, summary.GetProperty("toDate").Money("fuel"));
        Assert.Equal(0m, summary.GetProperty("toDate").Money("maintenance"));
        Assert.Equal(55_283.13m, summary.GetProperty("toDate").Money("total"));
        Assert.Equal(182.5m, summary.Money("liters"));
        Assert.Equal(52_500m, summary.Money("outstanding"));
        Assert.Equal(0, summary.GetProperty("pendingReviewCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("returnedCount").GetInt32());
        var line = Assert.Single(summary.GetProperty("machines").EnumerateArray());
        Assert.Equal("EX-210", line.GetProperty("assetNo").GetString());
        Assert.Equal(55_283.13m, line.Money("total"));

        // The manager has no equipment See money: no summary.
        var managerSummary = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectA}/equipment-summary", cast.Manager);
        Assert.Equal(HttpStatusCode.Forbidden, managerSummary.StatusCode);
    }

    [DatabaseFact]
    public async Task New_rental_terms_close_the_open_ones_the_day_before_and_never_change_a_recorded_cost()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var lessor = await api.CreateSupplierAsync(cast.Executive, "Nile Cranes");
        var machine = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("CR-03", "Rented",
            new { supplierId = lessor, rateBasis = "Day", rate = 3_500m, startDate = "2026-01-01" }));
        await api.AssignedAsync(cast.Manager, cast.ProjectA, machine.Id(), "2026-01-01");
        var cost = await api.RecordedCostAsync(cast.Manager, cast.ProjectA, FleetApi.RentalCostBody(machine.Id(), lessor, 15m, 3_500m));

        var response = await api.SendAsync(HttpMethod.Post, $"/api/equipment/{machine.Id()}/rentals", cast.Manager,
            new { supplierId = lessor, rateBasis = "Day", rate = 4_000m, startDate = "2026-02-01" });
        await EnsureStatus(response, HttpStatusCode.Created);

        var details = await api.GetEquipmentAsync(cast.Executive, machine.Id());
        var rentals = details.GetProperty("rentals").EnumerateArray().ToList();
        Assert.Equal(2, rentals.Count);
        Assert.Equal(4_000m, details.GetProperty("currentRental").Money("rate"));
        var closed = rentals.Single(rental => !rental.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("2026-01-31", closed.GetProperty("endDate").GetString());

        var unchanged = Assert.Single((await api.ListCostsAsync(cast.Executive, cast.ProjectA)).GetProperty("items").EnumerateArray());
        Assert.Equal(cost.Id(), unchanged.Id());
        Assert.Equal(3_500m, unchanged.Money("rate"));
        Assert.Equal(52_500m, unchanged.Money("amount"));

        // Rental terms are for rented machines only.
        var owned = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("TL-55"));
        var onOwned = await api.SendAsync(HttpMethod.Post, $"/api/equipment/{owned.Id()}/rentals", cast.Executive,
            new { supplierId = lessor, rateBasis = "Day", rate = 100m, startDate = "2026-02-01" });
        Assert.Equal(HttpStatusCode.Conflict, onOwned.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(onOwned));
    }

    [DatabaseFact]
    public async Task Without_see_money_a_caller_lists_only_their_own_costs()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var machine = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("PMP-12"));
        await api.AssignedAsync(cast.Manager, cast.ProjectA, machine.Id(), "2026-01-05");
        await api.RecordedCostAsync(cast.Manager, cast.ProjectA, FleetApi.FuelBody(machine.Id(), 20m, 15m));
        await api.RecordedCostAsync(cast.Executive, cast.ProjectA, FleetApi.FuelBody(machine.Id(), 30m, 15m));

        Assert.Equal(1, (await api.ListCostsAsync(cast.Manager, cast.ProjectA)).GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await api.ListCostsAsync(cast.Accountant, cast.ProjectA)).GetProperty("totalCount").GetInt32());

        // The site engineer has equipment View only: they see the machines, and record nothing.
        Assert.Single(await api.ProjectEquipmentAsync(cast.Engineer, cast.ProjectA));
        var engineerRecords = await api.RecordCostAsync(cast.Engineer, cast.ProjectA, FleetApi.FuelBody(machine.Id(), 10m, 15m));
        Assert.Equal(HttpStatusCode.Forbidden, engineerRecords.StatusCode);
    }
}
