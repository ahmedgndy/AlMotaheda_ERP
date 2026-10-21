using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

internal sealed partial class Demo
{
    private int _rejectedClaimReferral;

    /// <summary>
    /// Vehicle expense claims by the driver of the day, each with an odometer photo and a receipt (labelled DEMO
    /// images), reviewed by the Accountant (fuel Approve, never the submitter): one approved (posted once to the
    /// pickup's custody), one waiting, one sent back for evidence, one rejected and referred to HR. Plus a violation.
    /// </summary>
    private async Task ClaimsAsync()
    {
        Log.Section("Vehicle expense claims (fuel)");
        await ClaimAsync("pickup-fuel-1", Pickup, new { type = "Fuel", expenseDate = Iso(today.AddDays(-20)), odometerKm = 48650m, fuelQuantity = 55m, unitPrice = 14.50m, vendorName = "محطة وقود الطريق الدائري (تجريبي)", supplierId = _supplierIds["fuel"], invoiceNo = "RRF-55120", priceItemId = _priceItemIds["diesel"] },
            "pickup diesel 55 L", review: "approve");
        await ClaimAsync("pickup-oil", Pickup, new { type = "Maintenance", expenseDate = Iso(today.AddDays(-12)), odometerKm = 48900m, amount = 1350m, vendorName = "مركز صيانة النصر (تجريبي)", invoiceNo = "NSC-3310", notes = "تغيير زيت وفلتر / Oil and filter change" },
            "pickup oil change", review: "request-evidence");
        await ClaimAsync("pickup-fuel-2", Pickup, new { type = "Fuel", expenseDate = Iso(today.AddDays(-3)), odometerKm = 49150m, fuelQuantity = 60m, unitPrice = 15.00m, vendorName = "محطة وقود الطريق الدائري (تجريبي)", supplierId = _supplierIds["fuel"], invoiceNo = "RRF-55871", priceItemId = _priceItemIds["diesel"] },
            "pickup diesel 60 L", review: null);
        var rejected = await ClaimAsync("truck-fuel-1", Truck, new { type = "Fuel", expenseDate = Iso(today.AddDays(-8)), odometerKm = 211980m, fuelQuantity = 120m, unitPrice = 14.75m, vendorName = "محطة وقود الطريق الدائري (تجريبي)", supplierId = _supplierIds["fuel"], invoiceNo = "RRF-55433", priceItemId = _priceItemIds["diesel"] },
            "truck diesel 120 L", review: "reject");

        if (rejected.Str("status") == "Rejected" && rejected["hrReferralId"] is null)
        {
            var referred = await As(Cast.Accountant).PostAsync($"/api/vehicle-expenses/{rejected.Id()}/refer-to-hr", new
            {
                subject = "إيصال وقود لا يطابق قراءة العداد / Fuel receipt does not match the odometer",
                details = $"{Marker} The truck covered 480 km since the last fill but the receipt shows 120 L. Please review with the driver.",
                rowVersion = rejected.RowVersion(),
            });
            _rejectedClaimReferral = (int?)referred["hrReferralId"] ?? 0;
            Log.New($"claim {rejected.Str("number")} referred to HR");
        }
        else
        {
            _rejectedClaimReferral = (int?)rejected["hrReferralId"] ?? 0;
            Log.Kept($"claim {rejected.Str("number")} referral");
        }

        // A speeding ticket recorded by the Executive; the driver is read from the history at that date.
        var violations = (await As(Cast.Ceo).GetAsync($"/api/vehicle-violations?vehicleId={VehicleId(Truck)}")).Items().ToList();
        if (!violations.Any(row => row.Str("ticketNo") == "TRF-DEMO-7781"))
        {
            await As(Cast.Ceo).PostAsync("/api/vehicle-violations", new
            {
                vehicleId = VehicleId(Truck),
                violationDate = Iso(today.AddDays(-15)),
                category = "Speeding",
                description = $"{Marker} مخالفة سرعة على الطريق الإقليمي / Speeding on the Regional Ring Road",
                amount = 500m,
                ticketNo = "TRF-DEMO-7781",
            });
            Log.New("violation TRF-DEMO-7781 (speeding, truck)");
        }
        else
        {
            Log.Kept("violation TRF-DEMO-7781");
        }
    }

    private async Task<JsonNode> ClaimAsync(string key, VehicleSpec vehicle, object fields, string label, string? review)
    {
        var driver = As(vehicle.Driver);
        var odometer = await UploadFleetAsync(driver, $"{key}-odometer", "Odometer");
        var invoice = await UploadFleetAsync(driver, $"{key}-receipt", "Invoice");

        var body = System.Text.Json.JsonSerializer.SerializeToNode(fields)!.AsObject();
        body["vehicleId"] = VehicleId(vehicle);
        body["odometerAttachmentId"] = odometer;
        body["invoiceAttachmentId"] = invoice;
        body["idempotencyKey"] = Key($"claim:{key}");
        body["notes"] = $"{Marker} {(string?)body["notes"] ?? label}";
        if (body["priceItemId"] is not null && body["priceJustification"] is null)
        {
            body["priceJustification"] = null;
        }

        var claim = await driver.PostAsync("/api/vehicle-expenses", body);
        var start = claim.Str("status");
        if (start == "Submitted" && review is not null)
        {
            var path = $"/api/vehicle-expenses/{claim.Id()}";
            var accountant = As(Cast.Accountant);
            claim = review switch
            {
                "approve" => await accountant.PostAsync($"{path}/approve", new { note = $"{Marker} receipt and odometer match", rowVersion = claim.RowVersion() }),
                "request-evidence" => await accountant.PostAsync($"{path}/request-evidence", new { note = "برجاء إرفاق صورة الفلتر القديم وكارت الصيانة / Add a photo of the old filter and the service card.", rowVersion = claim.RowVersion() }),
                _ => await accountant.PostAsync($"{path}/reject", new { note = "كمية الوقود أكبر من المسافة المقطوعة منذ آخر تموين / More fuel than the distance since the last fill allows.", rowVersion = claim.RowVersion() }),
            };
            Log.New($"claim {claim.Str("number")} {label} {(decimal?)claim["amount"]:N2}: {start} -> {claim.Str("status")}");
        }
        else
        {
            Log.Kept($"claim {claim.Str("number")} {label} {(decimal?)claim["amount"]:N2}: {start}");
        }

        return claim;
    }

    /// <summary>Uploads one DEMO evidence photo (the same key returns the first upload).</summary>
    private async Task<int> UploadFleetAsync(Session session, string key, string purpose)
    {
        var attachment = await session.UploadAsync("/api/fleet/attachments", new Dictionary<string, string>
        {
            ["purpose"] = purpose,
            ["idempotencyKey"] = Key($"fleet-file:{key}"),
            ["capturedAt"] = today.ToDateTime(new TimeOnly(9, 30), DateTimeKind.Utc).ToString("O"),
        }, $"DEMO-{key}.png", DemoPng($"{purpose} {key}"), "image/png");
        return attachment.Id();
    }
}
