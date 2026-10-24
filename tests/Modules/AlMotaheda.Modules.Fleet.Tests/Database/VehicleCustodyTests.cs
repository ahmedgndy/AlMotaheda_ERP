using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Projects.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Fleet.Tests.Database;

/// <summary>
/// Vehicles on SQL Server: a vehicle's operating custody is funded through Finance with the driver accountable; the
/// driver claims with evidence; the Accountant approves once (also under concurrency), rejects keeping everything, or
/// refers to HR, which alone decides whether anything is deducted; and drivers change without rewriting history.
/// Vehicle GPS is in <see cref="VehicleGpsTrackingTests"/>.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed partial class VehicleCustodyTests(SqlServerFixture database)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, .. new byte[48], 0xFF, 0xD9];

    [DatabaseFact]
    public async Task A_vehicle_custody_is_funded_with_the_driver_accountable_claimed_with_evidence_and_posted_once()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await VehicleCastAsync(api);
        var today = Today(api);

        // H: 2,000 from the Accountant's custody into the vehicle's, received by the custody reviewer; the driver of the day is on the entry.
        var transfer = await Transferred(api, cast, VehicleFunding(cast.AccountantAccount, cast.VehicleId, 2_000m));
        Assert.Equal(("Received", "Custody"), (transfer.GetProperty("status").GetString(), transfer.GetProperty("destinationKind").GetString()));
        var custody = await Custody(api, cast.Accountant, cast.VehicleId);
        Assert.Equal((2_000m, 0m, 0), (Figure(custody, "posted"), Figure(custody, "pending"), custody.GetProperty("figures").GetProperty("pendingCount").GetInt32()));
        var funding = Assert.Single(custody.GetProperty("lines").EnumerateArray());
        Assert.Equal(("Funding", "Posted", cast.DriverEmployee), (funding.GetProperty("kind").GetString(), funding.GetProperty("status").GetString(), funding.GetProperty("driver").GetProperty("employeeId").GetInt32()));
        var vehicleAccount = transfer.GetProperty("destinationAccountId").GetInt32();
        Assert.Equal(8_000m, (await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{cast.AccountantAccount}", cast.Accountant))).GetProperty("account").GetProperty("posted").GetDecimal());

        // The driver sees their vehicle's custody; the engineer (fleet View, no money) sees the vehicle but no figures.
        Assert.Equal(2_000m, Figure(await Custody(api, cast.Driver, cast.VehicleId), "posted"));
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{cast.VehicleId}/custody", cast.Engineer)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{cast.VehicleId}", cast.Engineer))).GetProperty("custodyPosted").ValueKind);

        // I: a 500 toll claim with the odometer photo and the receipt: pending exposure, the balance untouched.
        var claim = await Submitted(api, cast.Driver, await ClaimBody(api, cast.Driver, cast.VehicleId, today.AddDays(-1), 10_100m, 500m));
        Assert.Equal(("Submitted", "NotPosted", 500m, 2), (claim.GetProperty("status").GetString(), claim.GetProperty("postingStatus").GetString(), claim.GetProperty("amount").GetDecimal(), claim.GetProperty("attachments").GetArrayLength()));
        Assert.StartsWith("VX-", claim.GetProperty("number").GetString());
        Assert.Equal(cast.DriverEmployee, claim.GetProperty("driver").GetProperty("employeeId").GetInt32());
        custody = await Custody(api, cast.Accountant, cast.VehicleId);
        Assert.Equal((2_000m, 500m, 1), (Figure(custody, "posted"), Figure(custody, "pending"), custody.GetProperty("figures").GetProperty("pendingCount").GetInt32()));
        var evidence = await api.SendAsync(HttpMethod.Get, $"/api/fleet/attachments/{claim.GetProperty("attachments")[0].Id()}/content", cast.Accountant);
        Assert.Equal((HttpStatusCode.OK, "image/jpeg"), (evidence.StatusCode, evidence.Content.Headers.ContentType?.MediaType));

        // J: the driver can't approve at all (no fuel Approve), the Executive who recorded nothing could, and a reviewer never
        // approves a claim they submitted; the Accountant approves: posted once, the balance is 1,500, the odometer moved.
        Assert.Equal(HttpStatusCode.Forbidden, (await Review(api, cast.Driver, claim, "approve")).StatusCode);
        var ownForm = await ClaimBody(api, cast.Executive, cast.VehicleId, today.AddDays(-2), 10_050m, 20m);
        ownForm["driverEmployeeId"] = cast.DriverEmployee;
        var ownClaim = await Submitted(api, cast.Executive, ownForm);
        Assert.Equal("own_record", await CodeOf(await Review(api, cast.Executive, ownClaim, "approve")));
        await Reviewed(api, cast.Accountant, ownClaim, "reject", "Recorded for the test");
        var approved = await Reviewed(api, cast.Accountant, claim, "approve", "Receipt matches");
        Assert.Equal(("Approved", "Posted", "Receipt matches"), (approved.GetProperty("status").GetString(), approved.GetProperty("postingStatus").GetString(), approved.GetProperty("reviewNote").GetString()));
        Assert.True(approved.GetProperty("custodyEntryId").ValueKind == JsonValueKind.Number);
        custody = await Custody(api, cast.Accountant, cast.VehicleId);
        Assert.Equal((1_500m, 0m, 500m), (Figure(custody, "posted"), Figure(custody, "pending"), Figure(custody, "spentPosted")));
        Assert.Equal(10_100m, (await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{cast.VehicleId}", cast.Accountant))).GetProperty("odometerKm").GetDecimal());
        Assert.Equal("invalid_status", await CodeOf(await Review(api, cast.Accountant, approved, "approve")));

        // Finance's registry reads the same figure from Fleet; the vehicle reports agree.
        var registered = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{vehicleAccount}", cast.Accountant))).GetProperty("account");
        Assert.Equal((1_500m, "Vehicle", "Fleet", cast.VehicleId), (registered.GetProperty("posted").GetDecimal(), registered.GetProperty("kind").GetString(), registered.GetProperty("ledgerOwner").GetString(), registered.GetProperty("referenceId").GetInt32()));
        var report = await Json(await api.SendAsync(HttpMethod.Get, "/api/reports/FLT-VEHICLE-CUSTODY", cast.Accountant));
        var row = report.GetProperty("rows").EnumerateArray().Select(entry => entry.GetProperty("cells")).Single(cells => cells.GetProperty("plate").GetString() == cast.Plate);
        Assert.Equal((2_000m, 500m, 1_500m), (row.GetProperty("funded").GetDecimal(), row.GetProperty("spent").GetDecimal(), row.GetProperty("posted").GetDecimal()));

        // K: four approvals of one claim at the same moment post one custody entry.
        var raced = await Submitted(api, cast.Driver, await ClaimBody(api, cast.Driver, cast.VehicleId, today, 10_150m, 100m));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Review(api, cast.Accountant, raced, "approve")));
        Assert.Equal(1, outcomes.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.All(outcomes.Where(response => response.StatusCode != HttpStatusCode.OK), response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable }));
        Assert.Equal(1, await api.FleetQueryAsync(db => db.VehicleCustodyEntries.CountAsync(entry => entry.SourceType == VehicleCustodySources.Expense && entry.SourceId == raced.Id())));
        Assert.Equal(1_400m, Figure(await Custody(api, cast.Accountant, cast.VehicleId), "posted"));

        // A reversal by someone else puts it back, with the reason; the claim can't be posted again.
        var posted = await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicle-expenses/{raced.Id()}", cast.Accountant));
        var reversed = await Reviewed(api, cast.Executive, posted, "reverse", reason: "Receipt was for another vehicle");
        Assert.Equal(("Reversed", "Receipt was for another vehicle"), (reversed.GetProperty("postingStatus").GetString(), reversed.GetProperty("reversalReason").GetString()));
        Assert.Equal(1_500m, Figure(await Custody(api, cast.Accountant, cast.VehicleId), "posted"));
    }

    [DatabaseFact]
    public async Task A_driver_picks_the_fuel_from_the_price_list_without_seeing_its_prices()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await VehicleCastAsync(api);
        foreach (var (ar, en, unit, category) in new[] { ("بنزين 95", "Petrol 95", "Liter", "Fuel"), ("سولار", "Diesel", "Liter", "Fuel"), ("أسمنت", "Cement", "Bag", "CementAggregates") })
        {
            await Created(api, cast.Executive, "/api/price-items", new
            {
                nameAr = ar, nameEn = en, classification = "Material", unit, categoryCode = category, kind = "Range", minPrice = 10m, maxPrice = 20m,
                currency = "EGP", effectiveFrom = Iso(Today(api).AddDays(-10)),
            });
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/price-items", cast.Driver)).StatusCode);
        var response = await api.SendAsync(HttpMethod.Get, "/api/fleet/fuel-types", cast.Driver);
        await EnsureStatus(response, HttpStatusCode.OK);
        var fuels = (await Json(response)).EnumerateArray().ToList();
        Assert.Equal(["Diesel", "Petrol 95"], fuels.Select(fuel => fuel.GetProperty("nameEn").GetString()));
        Assert.All(fuels, fuel => Assert.False(fuel.TryGetProperty("minPrice", out _)));
    }

    [DatabaseFact]
    public async Task The_driver_asks_for_cash_into_the_vehicle_and_the_usual_chain_pays_it()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await VehicleCastAsync(api);
        object Ask(decimal amount, string key) => new { accountKind = "Vehicle", referenceId = cast.VehicleId, amount, notes = "Fuel for the week", idempotencyKey = key };

        // Before any funding the vehicle has no cash account: the driver is told who opens it.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(HttpMethod.Post, "/api/custody-top-ups", cast.Driver, Ask(500m, Guid.NewGuid().ToString()))).StatusCode);

        await Transferred(api, cast, VehicleFunding(cast.AccountantAccount, cast.VehicleId, 1_000m));
        var key = Guid.NewGuid().ToString();
        var asked = await Created(api, cast.Driver, "/api/custody-top-ups", Ask(500m, key));
        Assert.Equal(("Requested", cast.AccountantAccount, 500m), (asked.GetProperty("status").GetString(), asked.GetProperty("sourceAccountId").GetInt32(), asked.GetProperty("amount").GetDecimal()));
        var again = await api.SendAsync(HttpMethod.Post, "/api/custody-top-ups", cast.Driver, Ask(500m, key));
        Assert.Equal((HttpStatusCode.OK, asked.Id()), (again.StatusCode, (await Json(again)).Id()));

        // Only the vehicle's driver asks; the driver sees the request; the requester never approves it.
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Post, "/api/custody-top-ups", cast.OtherDriver, Ask(100m, Guid.NewGuid().ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, $"/api/custody-transfers/{asked.Id()}", cast.Driver)).StatusCode);

        // The Executive approves, the accountant pays out and confirms: the vehicle holds 1,500.
        var transfer = asked;
        foreach (var (session, step) in new[] { (cast.Executive, "approve"), (cast.Accountant, "disburse"), (cast.Accountant, "receive") })
        {
            var response = await api.SendAsync(HttpMethod.Post, $"/api/custody-transfers/{transfer.Id()}/{step}", session, new { rowVersion = transfer.GetProperty("rowVersion").GetString() });
            await EnsureStatus(response, HttpStatusCode.OK);
            transfer = await Json(response);
        }

        Assert.Equal("Received", transfer.GetProperty("status").GetString());
        Assert.Equal(1_500m, Figure(await Custody(api, cast.Driver, cast.VehicleId), "posted"));
    }

    [DatabaseFact]
    public async Task A_claim_without_an_odometer_reading_is_accepted_with_its_photo_and_leaves_the_vehicle_reading_alone()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await VehicleCastAsync(api);
        await Transferred(api, cast, VehicleFunding(cast.AccountantAccount, cast.VehicleId, 1_000m));

        var body = await ClaimBody(api, cast.Driver, cast.VehicleId, Today(api).AddDays(-1), 0m, 300m);
        body.Remove("odometerKm");
        var claim = await Submitted(api, cast.Driver, body);
        Assert.Equal((JsonValueKind.Null, 0), (claim.GetProperty("odometerKm").ValueKind, claim.GetProperty("flags").GetArrayLength()));

        var approved = await Reviewed(api, cast.Accountant, claim, "approve", "Photo checked");
        Assert.Equal(("Approved", "Posted"), (approved.GetProperty("status").GetString(), approved.GetProperty("postingStatus").GetString()));
        Assert.Equal(10_000m, (await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{cast.VehicleId}", cast.Accountant))).GetProperty("odometerKm").GetDecimal());
        Assert.Equal(700m, Figure(await Custody(api, cast.Accountant, cast.VehicleId), "posted"));

        // The odometer photo is still required.
        var noPhoto = await ClaimBody(api, cast.Driver, cast.VehicleId, Today(api).AddDays(-1), 0m, 50m);
        noPhoto.Remove("odometerKm");
        noPhoto.Remove("odometerAttachmentId");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Driver, noPhoto)).StatusCode);
    }

    [DatabaseFact]
    public async Task A_rejected_claim_keeps_its_reason_and_evidence_and_hr_alone_decides_a_deduction_through_its_own_flow()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await VehicleCastAsync(api);
        var today = Today(api);
        await Transferred(api, cast, VehicleFunding(cast.AccountantAccount, cast.VehicleId, 2_000m));
        var month = new DateOnly(today.Year, today.Month, 1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

        // L: rejected with a note: the evidence and the history stay; the pending entry is released; the claim is final.
        var claim = await Submitted(api, cast.Driver, await ClaimBody(api, cast.Driver, cast.VehicleId, today.AddDays(-1), 10_100m, 500m));
        Assert.Equal(HttpStatusCode.BadRequest, (await Review(api, cast.Accountant, claim, "reject")).StatusCode);
        var rejected = await Reviewed(api, cast.Accountant, claim, "reject", "Receipt is not for this vehicle");
        Assert.Equal(("Rejected", "NotPosted", "Receipt is not for this vehicle", 2), (rejected.GetProperty("status").GetString(), rejected.GetProperty("postingStatus").GetString(), rejected.GetProperty("reviewNote").GetString(), rejected.GetProperty("attachments").GetArrayLength()));
        Assert.Equal("invalid_status", await CodeOf(await Review(api, cast.Accountant, rejected, "approve")));
        var custody = await Custody(api, cast.Accountant, cast.VehicleId);
        Assert.Equal((2_000m, 0m), (Figure(custody, "posted"), Figure(custody, "pending")));
        Assert.Equal("Released", custody.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("kind").GetString() == "Expense").GetProperty("status").GetString());
        Assert.Equal(2, await api.FleetQueryAsync(db => db.Attachments.CountAsync(attachment => attachment.OwnerId == rejected.Id())));

        // Referred to HR: an open case for the driver, nothing deducted by anyone yet (M).
        var referred = await Reviewed(api, cast.Accountant, rejected, "refer-to-hr", details: "Second receipt from another vehicle this month");
        var referralId = referred.GetProperty("hrReferralId").GetInt32();
        var referral = await Json(await api.SendAsync(HttpMethod.Get, $"/api/hr/referrals/{referralId}", cast.Executive));
        Assert.Equal(("Open", "VehicleExpense", rejected.Id(), cast.DriverEmployee, 500m), (referral.GetProperty("status").GetString(), referral.GetProperty("sourceType").GetString(),
            referral.GetProperty("sourceId").GetInt32(), referral.GetProperty("employeeId").GetInt32(), referral.GetProperty("amount").GetDecimal()));
        Assert.Equal("already_referred", await CodeOf(await Review(api, cast.Accountant, referred, "refer-to-hr", details: "Again")));
        Assert.Empty(await PayrollInputs(api, cast.Executive, cast.DriverEmployee, month));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/hr/referrals/{referralId}", cast.Driver)).StatusCode);

        // N: HR closes it with a deduction, raised through the payroll flow; a second case closed with no action adds nothing.
        var investigating = await Json(await api.SendAsync(HttpMethod.Post, $"/api/hr/referrals/{referralId}/start-investigation", cast.Executive, new { note = "Asked the driver", rowVersion = referral.GetProperty("rowVersion").GetString() }));
        Assert.Equal("UnderInvestigation", investigating.GetProperty("status").GetString());
        var closed = await api.SendAsync(HttpMethod.Post, $"/api/hr/referrals/{referralId}/close", cast.Executive,
            new { outcome = "DeductionApplied", note = "Driver admitted it", deduction = new { month, amount = 500m, reason = "Vehicle expense claimed without a valid receipt" }, rowVersion = investigating.GetProperty("rowVersion").GetString() });
        await EnsureStatus(closed, HttpStatusCode.OK);
        var decided = await Json(closed);
        Assert.Equal(("Closed", "DeductionApplied"), (decided.GetProperty("status").GetString(), decided.GetProperty("outcome").GetString()));
        var deduction = Assert.Single(await PayrollInputs(api, cast.Executive, cast.DriverEmployee, month));
        Assert.Equal(("Deduction", 500m, decided.GetProperty("deductionInputId").GetInt32()), (deduction.GetProperty("kind").GetString(), deduction.GetProperty("amount").GetDecimal(), deduction.Id()));
        Assert.Equal(referralId, (await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicle-expenses/{rejected.Id()}", cast.Accountant))).GetProperty("hrReferralId").GetInt32());

        var violation = await Created(api, cast.Executive, "/api/vehicle-violations",
            new { vehicleId = cast.VehicleId, violationDate = Iso(today.AddDays(-2)), category = "Speeding", description = "Radar on the ring road", amount = 300m, ticketNo = "TK-5512" });
        Assert.Equal((cast.DriverEmployee, "Recorded"), (violation.GetProperty("driver").GetProperty("employeeId").GetInt32(), violation.GetProperty("status").GetString()));
        Assert.StartsWith("VV-", violation.GetProperty("number").GetString());
        Assert.Equal("own_record", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/vehicle-violations/{violation.Id()}/review", cast.Executive, new { outcome = "Resolved", note = "Paid", rowVersion = violation.GetProperty("rowVersion").GetString() })));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/vehicle-violations/{violation.Id()}/review", cast.Manager, new { outcome = "Resolved", note = "Paid", rowVersion = violation.GetProperty("rowVersion").GetString() })).StatusCode);
        var resolved = await Json(await api.SendAsync(HttpMethod.Post, $"/api/vehicle-violations/{violation.Id()}/review", cast.Accountant, new { outcome = "Resolved", note = "Ticket paid by the company", resolution = "Paid 2026-10-15", rowVersion = violation.GetProperty("rowVersion").GetString() }));
        Assert.Equal("Resolved", resolved.GetProperty("status").GetString());
        var violationReferred = await Json(await api.SendAsync(HttpMethod.Post, $"/api/vehicle-violations/{violation.Id()}/refer-to-hr", cast.Accountant, new { details = "Repeated speeding", rowVersion = resolved.GetProperty("rowVersion").GetString() }));
        var secondReferral = await Json(await api.SendAsync(HttpMethod.Get, $"/api/hr/referrals/{violationReferred.GetProperty("hrReferralId").GetInt32()}", cast.Executive));
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/hr/referrals/{secondReferral.Id()}/close", cast.Executive,
            new { outcome = "NoAction", note = "First offence, warned verbally", rowVersion = secondReferral.GetProperty("rowVersion").GetString() }), HttpStatusCode.OK);
        Assert.Single(await PayrollInputs(api, cast.Executive, cast.DriverEmployee, month));
        var referrals = await Json(await api.SendAsync(HttpMethod.Get, "/api/hr/referrals", cast.Executive));
        Assert.Equal(2, referrals.GetProperty("countsByStatus").GetProperty("Closed").GetInt32());
    }

    [DatabaseFact]
    public async Task Only_the_driver_of_the_day_claims_and_a_driver_change_rewrites_no_history()
    {
        // The company policy refuses a price above its range outright (Block) for the fuel claim at the end.
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:PriceCheckPolicy"] = "Block" });
        var cast = await VehicleCastAsync(api);
        var today = Today(api);
        await Transferred(api, cast, VehicleFunding(cast.AccountantAccount, cast.VehicleId, 2_000m));
        var first = await Submitted(api, cast.Driver, await ClaimBody(api, cast.Driver, cast.VehicleId, today.AddDays(-5), 10_050m, 200m));

        // O: the other driver isn't on this vehicle; a fleet manager may name the real driver, never someone else.
        var stranger = await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.OtherDriver, await ClaimBody(api, cast.OtherDriver, cast.VehicleId, today.AddDays(-1), 10_100m, 50m));
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        Assert.Equal("forbidden", await CodeOf(stranger));
        var forDriver = await ClaimBody(api, cast.Executive, cast.VehicleId, today.AddDays(-1), 10_100m, 50m);
        forDriver["driverEmployeeId"] = cast.OtherDriverEmployee;
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Executive, forDriver)).StatusCode);
        forDriver["driverEmployeeId"] = cast.DriverEmployee;
        forDriver["idempotencyKey"] = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Executive, forDriver)).StatusCode);
        var managerNames = await ClaimBody(api, cast.Manager, cast.VehicleId, today.AddDays(-1), 10_100m, 50m);
        managerNames["driverEmployeeId"] = cast.DriverEmployee;
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Manager, managerNames)).StatusCode);

        // The driver sees only their own claims; the Accountant sees every one.
        Assert.Equal(2, (await Json(await api.SendAsync(HttpMethod.Get, "/api/vehicle-expenses", cast.Driver))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Json(await api.SendAsync(HttpMethod.Get, "/api/vehicle-expenses", cast.OtherDriver))).GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await Json(await api.SendAsync(HttpMethod.Get, "/api/vehicle-expenses", cast.Accountant))).GetProperty("totalCount").GetInt32());

        // P: the first driver hands over yesterday; the hand-over records the custody at that moment; the second driver starts today.
        var drivers = await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{cast.VehicleId}/drivers", cast.Manager));
        var current = Assert.Single(drivers.GetProperty("items").EnumerateArray());
        var overlapping = await api.SendAsync(HttpMethod.Post, $"/api/vehicles/{cast.VehicleId}/drivers", cast.Executive, new { driverEmployeeId = cast.OtherDriverEmployee, startDate = Iso(today) });
        Assert.Equal("driver_assigned", await CodeOf(overlapping));
        var ended = await Json(await api.SendAsync(HttpMethod.Post, $"/api/vehicles/{cast.VehicleId}/drivers/{current.Id()}/end", cast.Executive,
            new { endDate = Iso(today.AddDays(-1)), endOdometerKm = 10_120m, handoverNote = "Keys and 2 fuel cards", rowVersion = current.GetProperty("rowVersion").GetString() }));
        Assert.Equal((Iso(today.AddDays(-1)), 2_000m, 250m), (ended.GetProperty("endDate").GetString(), ended.GetProperty("handoverCustodyBalance").GetDecimal(), ended.GetProperty("handoverPendingClaims").GetDecimal()));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(HttpMethod.Post, $"/api/vehicles/{cast.VehicleId}/drivers", cast.Executive, new { driverEmployeeId = cast.OtherDriverEmployee, startDate = Iso(today.AddDays(-3)) })).StatusCode);
        var next = await Json(await api.SendAsync(HttpMethod.Post, $"/api/vehicles/{cast.VehicleId}/drivers", cast.Executive, new { driverEmployeeId = cast.OtherDriverEmployee, startDate = Iso(today) }));
        Assert.Equal(cast.OtherDriverEmployee, next.GetProperty("driver").GetProperty("employeeId").GetInt32());

        // Old claims keep the first driver; the second driver can't claim a day before their time, and can claim today.
        Assert.Equal(cast.DriverEmployee, (await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicle-expenses/{first.Id()}", cast.Accountant))).GetProperty("driver").GetProperty("employeeId").GetInt32());
        var backdated = await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.OtherDriver, await ClaimBody(api, cast.OtherDriver, cast.VehicleId, today.AddDays(-2), 10_110m, 60m));
        Assert.Equal(HttpStatusCode.Forbidden, backdated.StatusCode);
        var fromToday = await Submitted(api, cast.OtherDriver, await ClaimBody(api, cast.OtherDriver, cast.VehicleId, today, 10_130m, 60m));
        Assert.Equal(cast.OtherDriverEmployee, fromToday.GetProperty("driver").GetProperty("employeeId").GetInt32());
        var nowOld = await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Driver, await ClaimBody(api, cast.Driver, cast.VehicleId, today, 10_140m, 60m));
        Assert.Equal(HttpStatusCode.Forbidden, nowOld.StatusCode);
        var history = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{cast.VehicleId}/drivers", cast.Manager))).GetProperty("items");
        Assert.Equal(2, history.GetArrayLength());

        // A fuel claim goes through Price Control (the vehicle's Diesel, total ÷ litres read): above the approved range the
        // company policy refuses it; inside it passes.
        await FuelItemAsync(api, cast, "سولار", "Diesel", 12m, 16m);
        Assert.Equal("price_above_range", await CodeOf(await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.OtherDriver,
            await FuelBody(api, cast.OtherDriver, cast.VehicleId, 800m, Read(800m, liters: 40m)))));
        var filled = await Submitted(api, cast.OtherDriver, await FuelBody(api, cast.OtherDriver, cast.VehicleId, 620m, Read(620m, liters: 40m)));
        Assert.Equal((620m, "WithinRange", 16m, 15.5m), (filled.GetProperty("amount").GetDecimal(), filled.GetProperty("priceCheck").GetString(),
            filled.GetProperty("referenceMaxPrice").GetDecimal(), filled.GetProperty("effectiveUnitPrice").GetDecimal()));
    }

    /// <summary>
    /// The usual Fleet cast plus: two staff users who are HR employees (drivers), a registered vehicle driven by the
    /// first since a month ago, the Cash treasury opened and the Accountant's custody funded with 10,000.
    /// </summary>
    private static async Task<VehicleCast> VehicleCastAsync(ProjectsApi api)
    {
        var cast = await api.CastAsync();
        var (driverUserId, driver) = await api.CreateSignedInUserAsync(cast.Executive, "staff");
        var (otherUserId, otherDriver) = await api.CreateSignedInUserAsync(cast.Executive, "staff");
        var department = (await Created(api, cast.Executive, "/api/hr/departments", new { nameAr = "الحركة", nameEn = "Transport" })).Id();
        var driverEmployee = (await Created(api, cast.Executive, "/api/hr/employees", Employee("Hany", "29005051234567", department, driverUserId))).Id();
        var otherEmployee = (await Created(api, cast.Executive, "/api/hr/employees", Employee("Samir", "29006061234567", department, otherUserId))).Id();
        await Created(api, cast.Executive, $"/api/hr/employees/{driverEmployee}/salaries", new { effectiveFrom = "2025-01-01", basicSalary = 6_000m });
        await Created(api, cast.Executive, $"/api/hr/employees/{otherEmployee}/salaries", new { effectiveFrom = "2025-01-01", basicSalary = 6_000m });

        var vehicle = await Created(api, cast.Executive, "/api/vehicles", new
        {
            assetNo = "PK-201", nameAr = "بيك أب 201", nameEn = "Pickup 201", plate = "ب ص ع 1234", type = "Pickup", modelYear = 2022, fuelType = "Diesel",
            ownership = "Owned", condition = "Good", odometerKm = 10_000m,
        });
        Assert.Equal(("PK-201", "NotConfigured"), (vehicle.GetProperty("assetNo").GetString(), vehicle.GetProperty("gpsStatus").GetString()));
        Assert.Equal(EquipmentCategory.Vehicle, await api.FleetQueryAsync(db => db.Equipment.Where(row => row.Id == vehicle.Id()).Select(row => row.Category).SingleAsync()));
        await Created(api, cast.Executive, $"/api/vehicles/{vehicle.Id()}/drivers", new { driverEmployeeId = driverEmployee, startDate = Iso(Today(api).AddDays(-30)), startOdometerKm = 10_000m });
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/vehicles/{vehicle.Id()}/drivers", cast.Manager, new { driverEmployeeId = otherEmployee })).StatusCode);

        var cash = (await Json(await api.SendAsync(HttpMethod.Get, "/api/treasury", cast.Executive))).GetProperty("accounts").EnumerateArray().Single(row => row.GetProperty("account").GetString() == "Cash");
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, "/api/treasury/Cash/opening-balance", cast.Executive,
            new { openingBalance = 100_000m, openingDate = "2026-01-01", rowVersion = cash.GetProperty("rowVersion").GetString() }), HttpStatusCode.OK);
        var account = (await Created(api, cast.Accountant, "/api/custody-accounts", new { kind = "Accountant", holderUserId = cast.Accountant.UserId, nameAr = "عهدة المحاسب", nameEn = "Accountant's custody" })).Id();
        var vehicleCast = new VehicleCast(cast.Executive, cast.Manager, cast.Engineer, cast.Accountant, driver, otherDriver, driverEmployee, otherEmployee, vehicle.Id(), vehicle.GetProperty("plate").GetString()!, account);
        await Transferred(api, vehicleCast, new
        {
            amount = 10_000m, purpose = "CustodyAdvance", source = new { kind = "Treasury", treasuryAccount = "Cash" }, destination = new { kind = "Custody", accountId = account }, idempotencyKey = Guid.NewGuid().ToString(),
        });
        return vehicleCast;
    }

    private static object Employee(string name, string nationalId, int departmentId, int userId) => new
    {
        nameAr = $"{name} بالعربية", nameEn = name, nationalId, jobTitle = "Driver", departmentId, kind = "Staff", hireDate = "2024-06-01", userId,
    };

    private static object VehicleFunding(int sourceAccountId, int vehicleId, decimal amount) => new
    {
        amount,
        purpose = "CustodyAdvance",
        source = new { kind = "Custody", accountId = sourceAccountId },
        destination = new { kind = "Custody", accountKind = "Vehicle", referenceId = vehicleId },
        notes = "Operating cash for the pickup",
        idempotencyKey = Guid.NewGuid().ToString(),
    };

    /// <summary>A transfer asked and handed out by the Accountant, approved by the Executive, received by the Accountant (holder or custody reviewer).</summary>
    private static async Task<JsonElement> Transferred(ProjectsApi api, VehicleCast cast, object body)
    {
        var transfer = await Created(api, cast.Accountant, "/api/custody-transfers", body);
        foreach (var (session, step) in new[] { (cast.Executive, "approve"), (cast.Accountant, "disburse"), (cast.Accountant, "receive") })
        {
            var response = await api.SendAsync(HttpMethod.Post, $"/api/custody-transfers/{transfer.Id()}/{step}", session, new { rowVersion = transfer.GetProperty("rowVersion").GetString() });
            await EnsureStatus(response, HttpStatusCode.OK);
            transfer = await Json(response);
        }

        return transfer;
    }

    /// <summary>A claim form with a fresh odometer photo and receipt uploaded by <paramref name="session"/>.</summary>
    private static async Task<Dictionary<string, object?>> ClaimBody(ProjectsApi api, Session session, int vehicleId, DateOnly expenseDate, decimal odometerKm, decimal? amount) => new()
    {
        ["vehicleId"] = vehicleId,
        ["type"] = "Toll",
        ["expenseDate"] = Iso(expenseDate),
        ["odometerKm"] = odometerKm,
        ["amount"] = amount,
        ["vendorName"] = "Ring road toll",
        ["invoiceNo"] = $"T-{Guid.NewGuid():N}"[..12],
        ["odometerAttachmentId"] = await UploadAsync(api, session, "Odometer"),
        ["invoiceAttachmentId"] = await UploadAsync(api, session, "Invoice"),
        ["idempotencyKey"] = Guid.NewGuid().ToString(),
    };

    /// <summary>Uploads a photo: <paramref name="bytes"/>, or a JPEG of its own (16 random bytes inside), so photos only match when a test means them to.</summary>
    private static async Task<int> UploadAsync(ProjectsApi api, Session session, string purpose, byte[]? bytes = null)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(purpose), "purpose");
        content.Add(new StringContent(Guid.NewGuid().ToString()), "idempotencyKey");
        content.Add(new StringContent(DateTime.UtcNow.ToString("O")), "capturedAt");
        if (bytes is null)
        {
            bytes = [.. Jpeg];
            Guid.NewGuid().ToByteArray().CopyTo(bytes, 20);
        }

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", $"{purpose.ToLowerInvariant()}.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/fleet/attachments") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var response = await api.Access.App.Client.SendAsync(request);
        await EnsureStatus(response, HttpStatusCode.Created);
        var attachment = await Json(response);
        Assert.Equal((purpose, "image/jpeg", (long)Jpeg.Length), (attachment.GetProperty("purpose").GetString(), attachment.GetProperty("contentType").GetString(), attachment.GetProperty("sizeBytes").GetInt64()));
        return attachment.Id();
    }

    private static Task<JsonElement> Submitted(ProjectsApi api, Session session, object body) => Created(api, session, "/api/vehicle-expenses", body);

    private static Task<HttpResponseMessage> Review(ProjectsApi api, Session session, JsonElement claim, string step, string? note = null, string? reason = null, string? details = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/vehicle-expenses/{claim.Id()}/{step}", session, new { note, reason, details, rowVersion = claim.GetProperty("rowVersion").GetString() });

    private static async Task<JsonElement> Reviewed(ProjectsApi api, Session session, JsonElement claim, string step, string? note = null, string? reason = null, string? details = null)
    {
        var response = await Review(api, session, claim, step, note, reason, details);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    private static async Task<JsonElement> Custody(ProjectsApi api, Session session, int vehicleId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{vehicleId}/custody", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    private static decimal Figure(JsonElement custody, string name) => custody.GetProperty("figures").GetProperty(name).GetDecimal();

    private static async Task<List<JsonElement>> PayrollInputs(ProjectsApi api, Session session, int employeeId, string month)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/hr/payroll-inputs?month={month}&employeeId={employeeId}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).EnumerateArray().ToList();
    }

    private static async Task<JsonElement> Created(ProjectsApi api, Session session, string path, object body)
    {
        var response = await api.SendAsync(HttpMethod.Post, path, session, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    private static DateOnly Today(ProjectsApi api) => api.Access.App.Services.GetRequiredService<CompanyClock>().Today;

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record VehicleCast(
        Session Executive, Session Manager, Session Engineer, Session Accountant, Session Driver, Session OtherDriver,
        int DriverEmployee, int OtherDriverEmployee, int VehicleId, string Plate, int AccountantAccount);
}
