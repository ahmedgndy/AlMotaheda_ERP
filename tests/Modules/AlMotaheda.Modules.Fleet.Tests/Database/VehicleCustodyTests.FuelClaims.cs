using System.Globalization;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Projects.Tests.Database;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Fleet.Tests.Database;

/// <summary>
/// The camera fuel claim on SQL Server: the fuel is the vehicle's, the claim is three photos and the total read on the
/// phone, the server computes and checks the price per litre against Price Control (never passing an incomplete check),
/// and a vehicle has one fuel claim waiting for review or correction at a time, also under simultaneous requests.
/// </summary>
public sealed partial class VehicleCustodyTests
{
    [DatabaseFact]
    public async Task A_fuel_claim_is_three_photos_and_the_read_total_with_the_vehicles_fuel_checked_by_the_server()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await VehicleCastAsync(api);
        await Transferred(api, cast, VehicleFunding(cast.AccountantAccount, cast.VehicleId, 2_000m));
        var diesel = await FuelItemAsync(api, cast, "سولار", "Diesel", 12m, 16m);

        // A: the vehicle's fuel (Diesel) is found in Price Control; nothing is chosen on the claim.
        var state = await FuelState(api, cast.Driver, cast.VehicleId);
        Assert.Equal(("Diesel", diesel, true, true, JsonValueKind.Null), (state.GetProperty("fuelType").GetString(), state.GetProperty("fuelItemId").GetInt32(),
            state.GetProperty("isDriver").GetBoolean(), state.GetProperty("canClaim").GetBoolean(), state.GetProperty("outstanding").ValueKind));

        // D: EGP 500.00 read from the display with 40 litres: the server computes 12.50 per litre and compares it with Diesel.
        var capturedAt = DateTime.UtcNow.AddMinutes(-90);
        var claim = await Submitted(api, cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 500m, Read(500m, liters: 40m, unitPrice: 12.5m), capturedAt));
        Assert.Equal(("Fuel", "Submitted", 500m, "Ocr", 3), (claim.GetProperty("type").GetString(), claim.GetProperty("status").GetString(), claim.GetProperty("amount").GetDecimal(),
            claim.GetProperty("amountSource").GetString(), claim.GetProperty("attachments").GetArrayLength()));
        Assert.Equal((diesel, "WithinRange", 12.5m, 40m, 16m), (claim.GetProperty("priceItemId").GetInt32(), claim.GetProperty("priceCheck").GetString(),
            claim.GetProperty("effectiveUnitPrice").GetDecimal(), claim.GetProperty("fuelQuantity").GetDecimal(), claim.GetProperty("referenceMaxPrice").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, claim.GetProperty("unitPrice").ValueKind);
        Assert.Equal(["PumpDisplay", "Receipt", "VehicleAtPump"], claim.GetProperty("attachments").EnumerateArray().Select(photo => photo.GetProperty("purpose").GetString()).Order());

        // The capture moment is kept as taken (the phone was offline); the server's receipt is its own moment, later.
        var kept = claim.GetProperty("capturedAt").GetDateTime().ToUniversalTime();
        Assert.True(Math.Abs((kept - capturedAt).TotalSeconds) < 1);
        Assert.True(claim.GetProperty("submittedAt").GetDateTime().ToUniversalTime() > kept.AddMinutes(60));
        Assert.Empty(claim.GetProperty("flags").EnumerateArray());
        Assert.Equal(500m, Figure(await Custody(api, cast.Accountant, cast.VehicleId), "pending"));

        // K: the vehicle's card shows the claim waiting; a second claim for the vehicle is refused, with the claim to open.
        state = await FuelState(api, cast.Driver, cast.VehicleId);
        Assert.Equal((false, claim.Id(), "Submitted", true), (state.GetProperty("canClaim").GetBoolean(), state.GetProperty("outstanding").Id(),
            state.GetProperty("outstanding").GetProperty("status").GetString(), state.GetProperty("outstanding").GetProperty("mine").GetBoolean()));
        var second = await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 300m, Read(300m)));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await Json(second);
        Assert.Equal(("fuel_claim_outstanding", claim.Id(), claim.GetProperty("number").GetString()), (problem.GetProperty("code").GetString(), problem.GetProperty("claimId").GetInt32(), problem.GetProperty("claimNumber").GetString()));

        // I: the phone's retry of the first claim (same key) returns it: no second claim, no second set of photos or alerts.
        var retryBody = await FuelBody(api, cast.Driver, cast.VehicleId, 500m, Read(500m, liters: 40m), capturedAt);
        retryBody["idempotencyKey"] = await api.FleetQueryAsync(db => db.VehicleExpenses.Where(row => row.Id == claim.Id()).Select(row => row.IdempotencyKey.ToString()).SingleAsync());
        var retry = await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Driver, retryBody);
        Assert.Equal((HttpStatusCode.OK, claim.Id()), (retry.StatusCode, (await Json(retry)).Id()));
        Assert.Equal(1, await api.FleetQueryAsync(db => db.VehicleExpenses.CountAsync(row => row.VehicleId == cast.VehicleId && row.Type == VehicleExpenseType.Fuel)));
        var alerts = await api.FleetQueryAsync(db => db.VehicleExpenseAlerts.CountAsync(alert => alert.ExpenseId == claim.Id()));

        // The rule is the vehicle's: a fleet manager on the driver's behalf is refused too, the other driver isn't the driver.
        var onBehalf = await FuelBody(api, cast.Executive, cast.VehicleId, 200m, Read(200m));
        onBehalf["driverEmployeeId"] = cast.DriverEmployee;
        Assert.Equal("fuel_claim_outstanding", await CodeOf(await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Executive, onBehalf)));
        var other = await FuelState(api, cast.OtherDriver, cast.VehicleId);
        Assert.Equal((false, false, false), (other.GetProperty("isDriver").GetBoolean(), other.GetProperty("canClaim").GetBoolean(), other.GetProperty("outstanding").GetProperty("mine").GetBoolean()));

        // M: approved, the vehicle can claim again.
        await Reviewed(api, cast.Accountant, claim, "approve", "Pump photo matches");
        Assert.True((await FuelState(api, cast.Driver, cast.VehicleId)).GetProperty("canClaim").GetBoolean());
        Assert.Equal("Approved", (await FuelState(api, cast.Driver, cast.VehicleId)).GetProperty("latest").GetProperty("status").GetString());
        Assert.Equal(alerts, await api.FleetQueryAsync(db => db.VehicleExpenseAlerts.CountAsync(alert => alert.ExpenseId == claim.Id())));

        // L: four simultaneous claims from two phones of the same vehicle (fast taps, two devices): exactly one lands.
        var bodies = new List<Dictionary<string, object?>>();
        for (var i = 0; i < 4; i++)
        {
            bodies.Add(await FuelBody(api, i % 2 == 0 ? cast.Driver : cast.Executive, cast.VehicleId, 100m + i, Read(100m + i)));
            if (i % 2 == 1)
            {
                bodies[i]["driverEmployeeId"] = cast.DriverEmployee;
            }
        }

        var raced = await Task.WhenAll(bodies.Select((body, i) => api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", i % 2 == 0 ? cast.Driver : cast.Executive, body)));
        Assert.Equal(1, raced.Count(response => response.StatusCode == HttpStatusCode.Created));
        Assert.All(raced.Where(response => response.StatusCode != HttpStatusCode.Created), response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable }));
        Assert.Equal(1, await api.FleetQueryAsync(db => db.VehicleExpenses.CountAsync(row => row.VehicleId == cast.VehicleId && row.Type == VehicleExpenseType.Fuel
                                                                                         && (row.Status == VehicleExpenseStatus.Submitted || row.Status == VehicleExpenseStatus.Escalated || row.Status == VehicleExpenseStatus.EvidenceRequested))));
        var winner = await Json(raced.Single(response => response.StatusCode == HttpStatusCode.Created));

        // M: rejected, the vehicle can claim again; another type of expense was never blocked by the fuel claim.
        await Submitted(api, cast.Driver, await ClaimBody(api, cast.Driver, cast.VehicleId, Today(api), 10_200m, 25m));
        await Reviewed(api, cast.Accountant, await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicle-expenses/{winner.Id()}", cast.Accountant)), "reject", "Duplicate fill");
        Assert.True((await FuelState(api, cast.Driver, cast.VehicleId)).GetProperty("canClaim").GetBoolean());
        var next = await Submitted(api, cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 450m, Read(450m, liters: 30m)));
        Assert.DoesNotContain("DuplicateReceipt", Kinds(next));
        await Reviewed(api, cast.Accountant, next, "reject", "Recorded for the test");

        // The same pump photo on another claim is shown to the reviewer (a flag, never a block).
        var reused = await FuelBody(api, cast.Driver, cast.VehicleId, 450m, Read(450m, liters: 30m));
        var bytes = await (await api.SendAsync(HttpMethod.Get, $"/api/fleet/attachments/{next.GetProperty("attachments").EnumerateArray().First(photo => photo.GetProperty("purpose").GetString() == "PumpDisplay").Id()}/content", cast.Driver)).Content.ReadAsByteArrayAsync();
        reused["pumpDisplayAttachmentId"] = await UploadAsync(api, cast.Driver, "PumpDisplay", bytes);
        Assert.Contains("DuplicateReceipt", Kinds(await Submitted(api, cast.Driver, reused)));
    }

    [DatabaseFact]
    public async Task A_fuel_claim_without_its_photos_or_with_typed_prices_or_an_unconfirmed_reading_is_refused()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await VehicleCastAsync(api);
        await Transferred(api, cast, VehicleFunding(cast.AccountantAccount, cast.VehicleId, 1_000m));
        await FuelItemAsync(api, cast, "سولار", "Diesel", 12m, 16m);

        async Task<HttpResponseMessage> Post(Action<Dictionary<string, object?>> change)
        {
            var body = await FuelBody(api, cast.Driver, cast.VehicleId, 500m, Read(500m, liters: 40m));
            change(body);
            return await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Driver, body);
        }

        // C, J: each of the three photos is required, and of its own kind (an odometer photo is not a pump display).
        foreach (var photo in new[] { "invoiceAttachmentId", "pumpDisplayAttachmentId", "vehicleAtPumpAttachmentId" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body.Remove(photo))).StatusCode);
        }

        var odometer = await UploadAsync(api, cast.Driver, "Odometer");
        Assert.Equal("evidence_required", await CodeOf(await Post(body => body["pumpDisplayAttachmentId"] = odometer)));

        // B: the removed fields can't come back through the API: no typed litres, price per litre or fuel item.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body["unitPrice"] = 12.5m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body["fuelQuantity"] = 40m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body["priceItemId"] = 1)).StatusCode);

        // The date is the capture moment's: none in the future, and a typed date must agree.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body["capturedAt"] = DateTime.UtcNow.AddHours(2).ToString("O"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body.Remove("capturedAt"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body["expenseDate"] = Iso(Today(api).AddDays(-3)))).StatusCode);

        // H: a reading is never taken on the phone's word: an "Ocr" amount must be the reading, with its confidence.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body["amount"] = 550m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body["reading"] = new { source = "Ocr", ocrAmount = 500m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(body => body.Remove("reading"))).StatusCode);
        Assert.Equal(0, await api.FleetQueryAsync(db => db.VehicleExpenses.CountAsync(row => row.VehicleId == cast.VehicleId)));

        // A corrected amount is accepted as ManualCorrection with the reading it replaced, and flagged for the reviewer.
        var corrected = await Submitted(api, cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 520m,
            new { source = "ManualCorrection", ocrAmount = 52m, confidence = 0.41m, ocrLiters = 40m, engine = "mlkit-latin", photoNotes = "receipt: blurry (kept after 2 retakes)" }));
        Assert.Equal(("ManualCorrection", 52m, 0.41m), (corrected.GetProperty("amountSource").GetString(), corrected.GetProperty("ocrAmount").GetDecimal(), corrected.GetProperty("ocrConfidence").GetDecimal()));
        Assert.Equal(["AmountManuallyEntered", "LowReadingConfidence", "PhotoQuality"], Kinds(corrected));
        Assert.Equal("WithinRange", corrected.GetProperty("priceCheck").GetString());
    }

    [DatabaseFact]
    public async Task An_incomplete_price_check_never_passes_and_the_central_price_applies_without_rewriting_history()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await VehicleCastAsync(api);
        await Transferred(api, cast, VehicleFunding(cast.AccountantAccount, cast.VehicleId, 2_000m));

        // O: no fuel item for Diesel yet: the check is Incomplete, flagged, and not a pass.
        var noItem = await Submitted(api, cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 500m, Read(500m, liters: 40m)));
        Assert.Equal(("Incomplete", JsonValueKind.Null), (noItem.GetProperty("priceCheck").GetString(), noItem.GetProperty("priceItemId").ValueKind));
        Assert.Equal(["PriceNotValidated"], Kinds(noItem));
        await Reviewed(api, cast.Accountant, noItem, "reject", "Recorded for the test");

        // With Diesel in Price Control but no litres on the display: Incomplete again, the item named.
        var diesel = await FuelItemAsync(api, cast, "سولار", "Diesel", 12m, 16m);
        var noLiters = await Submitted(api, cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 500m, Read(500m)));
        Assert.Equal(("Incomplete", diesel, JsonValueKind.Null), (noLiters.GetProperty("priceCheck").GetString(), noLiters.GetProperty("priceItemId").GetInt32(), noLiters.GetProperty("effectiveUnitPrice").ValueKind));
        Assert.Equal(["PriceNotValidated"], Kinds(noLiters));
        var requested = await Reviewed(api, cast.Accountant, noLiters, "request-evidence", "The pump display is cut off: photograph it again");

        // N: returned for correction, the vehicle has no room for a second claim; the same claim is corrected with a new pump photo.
        Assert.Equal("EvidenceRequested", (await FuelState(api, cast.Driver, cast.VehicleId)).GetProperty("outstanding").GetProperty("status").GetString());
        Assert.Equal("fuel_claim_outstanding", await CodeOf(await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 500m, Read(500m, liters: 40m)))));
        var pump = await UploadAsync(api, cast.Driver, "PumpDisplay");
        var resubmitted = await api.SendAsync(HttpMethod.Post, $"/api/vehicle-expenses/{noLiters.Id()}/resubmit", cast.Driver,
            new { pumpDisplayAttachmentId = pump, amount = 510m, reading = Read(510m, liters: 34m), rowVersion = requested.GetProperty("rowVersion").GetString() });
        await EnsureStatus(resubmitted, HttpStatusCode.OK);
        var fixedClaim = await Json(resubmitted);
        Assert.Equal((noLiters.Id(), "Submitted", 2, 510m, "WithinRange", 15m), (fixedClaim.Id(), fixedClaim.GetProperty("status").GetString(), fixedClaim.GetProperty("round").GetInt32(),
            fixedClaim.GetProperty("amount").GetDecimal(), fixedClaim.GetProperty("priceCheck").GetString(), fixedClaim.GetProperty("effectiveUnitPrice").GetDecimal()));
        Assert.Equal(4, fixedClaim.GetProperty("attachments").GetArrayLength());
        Assert.Equal(510m, Figure(await Custody(api, cast.Accountant, cast.VehicleId), "pending"));
        var approved = await Reviewed(api, cast.Accountant, fixedClaim, "approve", "Display readable now");

        // P: the central range changes to 10–11; the approved claim keeps the comparison it was made with.
        var range = await Created(api, cast.Manager, $"/api/price-items/{diesel}/ranges", new { minPrice = 10m, maxPrice = 11m, reason = "New official price", effectiveFrom = Iso(Today(api).AddDays(-1)) });
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{range.Id()}/approve", cast.Executive, new { }), HttpStatusCode.OK);
        var history = await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicle-expenses/{approved.Id()}", cast.Accountant));
        Assert.Equal(("WithinRange", 16m, 15m, "Approved"), (history.GetProperty("priceCheck").GetString(), history.GetProperty("referenceMaxPrice").GetDecimal(),
            history.GetProperty("effectiveUnitPrice").GetDecimal(), history.GetProperty("status").GetString()));

        // The next fill at 15 per litre is now above the range: the vehicle is on no project, so the Executive decides the claim.
        var above = await Submitted(api, cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 600m, Read(600m, liters: 40m)));
        Assert.Equal(("AboveRange", "Escalated", 11m), (above.GetProperty("priceCheck").GetString(), above.GetProperty("status").GetString(), above.GetProperty("referenceMaxPrice").GetDecimal()));
        Assert.Contains("PriceAboveRange", Kinds(above));
        Assert.Equal(HttpStatusCode.Forbidden, (await Review(api, cast.Driver, above, "approve")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_petrol_vehicle_is_compared_with_the_grade_its_fleet_manager_links()
    {
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:PriceCheckPolicy"] = "Block" });
        var cast = await VehicleCastAsync(api);
        await Transferred(api, cast, VehicleFunding(cast.AccountantAccount, cast.VehicleId, 2_000m));
        await FuelItemAsync(api, cast, "بنزين 92", "Petrol 92", 15m, 17.5m);
        var petrol95 = await FuelItemAsync(api, cast, "بنزين 95", "Petrol 95", 17m, 19.5m);
        var vehicle = await Json(await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{cast.VehicleId}", cast.Executive));
        var form = new Dictionary<string, object?>
        {
            ["nameAr"] = vehicle.GetProperty("nameAr").GetString(), ["nameEn"] = vehicle.GetProperty("nameEn").GetString(), ["plate"] = vehicle.GetProperty("plate").GetString(),
            ["type"] = "Pickup", ["fuelType"] = "Petrol", ["rowVersion"] = vehicle.GetProperty("rowVersion").GetString(),
        };
        vehicle = await Json(await api.SendAsync(HttpMethod.Put, $"/api/vehicles/{cast.VehicleId}", cast.Executive, form));

        // Two petrol grades and no link: which one is unknown, so the price is not validated.
        Assert.Equal(JsonValueKind.Null, (await FuelState(api, cast.Driver, cast.VehicleId)).GetProperty("fuelItemId").ValueKind);
        var unknown = await Submitted(api, cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 360m, Read(360m, liters: 20m)));
        Assert.Equal("Incomplete", unknown.GetProperty("priceCheck").GetString());
        await Reviewed(api, cast.Accountant, unknown, "reject", "Recorded for the test");

        // The fleet manager links Petrol 95; a later edit that doesn't name it leaves it linked.
        form["fuelPriceItemId"] = 999_999;
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(HttpMethod.Put, $"/api/vehicles/{cast.VehicleId}", cast.Executive, form)).StatusCode);
        form["fuelPriceItemId"] = petrol95;
        form["rowVersion"] = vehicle.GetProperty("rowVersion").GetString();
        vehicle = await Json(await api.SendAsync(HttpMethod.Put, $"/api/vehicles/{cast.VehicleId}", cast.Executive, form));
        form.Remove("fuelPriceItemId");
        form["notes"] = "Fuel card 7";
        form["rowVersion"] = vehicle.GetProperty("rowVersion").GetString();
        vehicle = await Json(await api.SendAsync(HttpMethod.Put, $"/api/vehicles/{cast.VehicleId}", cast.Executive, form));
        Assert.Equal(petrol95, vehicle.GetProperty("fuelPriceItemId").GetInt32());
        Assert.Equal(petrol95, (await FuelState(api, cast.Driver, cast.VehicleId)).GetProperty("fuelItemId").GetInt32());

        // Under Block, 25 per litre is refused; 18.5 passes as Petrol 95 (it would be above Petrol 92's range).
        Assert.Equal("price_above_range", await CodeOf(await api.SendAsync(HttpMethod.Post, "/api/vehicle-expenses", cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 500m, Read(500m, liters: 20m)))));
        var filled = await Submitted(api, cast.Driver, await FuelBody(api, cast.Driver, cast.VehicleId, 370m, Read(370m, liters: 20m)));
        Assert.Equal((petrol95, "WithinRange", 18.5m), (filled.GetProperty("priceItemId").GetInt32(), filled.GetProperty("priceCheck").GetString(), filled.GetProperty("effectiveUnitPrice").GetDecimal()));
    }

    private static object Read(decimal amount, decimal? liters = null, decimal? unitPrice = null, decimal confidence = 0.97m) =>
        new { source = "Ocr", ocrAmount = amount, ocrLiters = liters, ocrUnitPrice = unitPrice, confidence, engine = "mlkit-latin", text = $"TOTAL {amount:0.00}" };

    /// <summary>A camera fuel claim: the receipt, the pump display and the vehicle at the pump uploaded by <paramref name="session"/>, and the reading.</summary>
    private static async Task<Dictionary<string, object?>> FuelBody(ProjectsApi api, Session session, int vehicleId, decimal amount, object reading, DateTime? capturedAt = null) => new()
    {
        ["vehicleId"] = vehicleId,
        ["type"] = "Fuel",
        ["capturedAt"] = (capturedAt ?? DateTime.UtcNow.AddMinutes(-5)).ToString("O", CultureInfo.InvariantCulture),
        ["amount"] = amount,
        ["reading"] = reading,
        ["invoiceAttachmentId"] = await UploadAsync(api, session, "Receipt"),
        ["pumpDisplayAttachmentId"] = await UploadAsync(api, session, "PumpDisplay"),
        ["vehicleAtPumpAttachmentId"] = await UploadAsync(api, session, "VehicleAtPump"),
        ["idempotencyKey"] = Guid.NewGuid().ToString(),
    };

    private static async Task<int> FuelItemAsync(ProjectsApi api, VehicleCast cast, string nameAr, string nameEn, decimal min, decimal max)
    {
        var created = await Created(api, cast.Executive, "/api/price-items", new
        {
            nameAr, nameEn, classification = "Material", unit = "Liter", categoryCode = "Fuel", kind = "Range", minPrice = min, maxPrice = max,
            currency = "EGP", effectiveFrom = Iso(Today(api).AddDays(-30)),
        });
        return created.TryGetProperty("item", out var item) ? item.Id() : created.Id();
    }

    private static async Task<JsonElement> FuelState(ProjectsApi api, Session session, int vehicleId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{vehicleId}/fuel-claim", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    private static List<string?> Kinds(JsonElement claim) => claim.GetProperty("flags").EnumerateArray().Select(flag => flag.GetProperty("kind").GetString()).Order().ToList();
}
