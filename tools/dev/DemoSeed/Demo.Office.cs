using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

internal sealed partial class Demo
{
    /// <summary>
    /// The Office Manager's petty cash (funded with 10,000 by the transfer above): the monthly internet subscription with
    /// its receipt photo (approved by the Accountant), copy paper waiting for review, an air-conditioner repair request
    /// approved before the work, a cash count, and a funding request waiting for Finance.
    /// </summary>
    private async Task OfficeAsync()
    {
        Log.Section("Office petty cash");
        var office = As(Cast.OfficeManager);
        var priceItems = (await office.GetAsync("/api/office/price-items")).Items().ToList();
        int? OfficePriceItem(string key) => priceItems.FirstOrDefault(item => item.Id() == _priceItemIds[key])?.Id();

        var existing = (await office.GetAsync($"/api/office/expenses?custodyId={_officeCustodyId}&pageSize=100")).Items().ToList();

        await OfficeExpenseAsync(existing, "internet-month", new
        {
            category = "Internet", priceItemId = OfficePriceItem("internet"), description = "اشتراك الإنترنت الشهري للمكتب الرئيسي / Head office monthly internet",
            purpose = "تشغيل المكتب / Office operations", vendor = "النور للاتصالات (تجريبي)", invoiceNo = "NTC-2026-10-0441",
            quantity = 1m, unit = "Subscription", unitPrice = 1450m, expenseDate = Iso(today.AddDays(-10)),
        }, "internet subscription 1,450", review: "approve");

        await OfficeExpenseAsync(existing, "copy-paper", new
        {
            category = "Stationery", priceItemId = OfficePriceItem("paper"), description = "ورق تصوير A4 - 10 رزم / A4 copy paper, 10 reams",
            purpose = "مستلزمات مكتبية / Office supplies", vendor = "مكتبة النهضة (تجريبي)", invoiceNo = "NHD-7781",
            quantity = 10m, unit = "Piece", unitPrice = 210m, expenseDate = Iso(today.AddDays(-2)),
        }, "copy paper 2,100 waiting for review", review: null);

        // A repair asked for before the work (approved estimate = a commitment, not an expense).
        var spends = (await office.GetAsync($"/api/office/spend-requests?custodyId={_officeCustodyId}")).Items().ToList();
        if (!spends.Any(row => row.Str("title").Contains("DEMO")))
        {
            var photo = await UploadOfficeAsync(office, "ac-before", "BeforeRepair");
            var spend = await office.PostAsync("/api/office/spend-requests", new
            {
                custodyId = _officeCustodyId,
                kind = "Repair",
                title = "صيانة تكييف غرفة الاجتماعات (DEMO) / Meeting room AC repair",
                description = "التكييف لا يبرد ويصدر صوتًا عاليًا / The AC does not cool and is noisy",
                location = "غرفة الاجتماعات - الدور الثاني / Meeting room, 2nd floor",
                asset = "تكييف 2.25 حصان / 2.25 HP split AC",
                supplier = "التبريد الحديث (تجريبي)",
                estimatedAmount = 2500m,
                isEmergency = false,
                attachmentIds = new[] { photo },
                idempotencyKey = Key("office-spend:ac"),
            });
            spend = await As(Cast.Accountant).PostAsync($"/api/office/spend-requests/{spend.Id()}/approve", new { note = $"{Marker} approved up to the estimate", rowVersion = spend.RowVersion() });
            Log.New($"office repair request: {spend.Str("status")}");
        }
        else
        {
            Log.Kept("office repair request");
        }

        var counts = (await office.GetAsync($"/api/office/cash-counts?custodyId={_officeCustodyId}")).Items().ToList();
        if (counts.Count == 0)
        {
            var photo = await UploadOfficeAsync(office, "count-1", "CashCount");
            await office.PostAsync("/api/office/cash-counts", new
            {
                custodyId = _officeCustodyId,
                countedAmount = 8550m,
                countedOn = Iso(today.AddDays(-1)),
                explanation = $"{Marker} جرد نهاية الأسبوع / End-of-week count",
                attachmentIds = new[] { photo },
                idempotencyKey = Key("office-count:1"),
            });
            Log.New("office cash count 8,550");
        }
        else
        {
            Log.Kept("office cash count");
        }

        var fundings = (await office.GetAsync($"/api/office/funding-requests?custodyId={_officeCustodyId}")).Items().ToList();
        if (fundings.Count == 0)
        {
            await office.PostAsync("/api/office/funding-requests", new
            {
                custodyId = _officeCustodyId,
                amount = 5000m,
                reason = $"{Marker} تغذية العهدة لمصروفات الشهر القادم / Funds for next month's office expenses",
                requestedDate = Iso(today),
                idempotencyKey = Key("office-funding:next-month"),
            });
            Log.New("office funding request 5,000 (waiting for Finance)");
        }
        else
        {
            Log.Kept("office funding request");
        }
    }

    private async Task OfficeExpenseAsync(List<JsonNode> existing, string key, object fields, string label, string? review)
    {
        var office = As(Cast.OfficeManager);
        var found = existing.FirstOrDefault(row => row.Str("invoiceNo") == (string?)System.Text.Json.JsonSerializer.SerializeToNode(fields)!["invoiceNo"]);
        if (found is null)
        {
            var receipt = await UploadOfficeAsync(office, $"{key}-receipt", "Receipt");
            var body = System.Text.Json.JsonSerializer.SerializeToNode(fields)!.AsObject();
            body["custodyId"] = _officeCustodyId;
            body["attachmentIds"] = new JsonArray(receipt);
            body["idempotencyKey"] = Key($"office-expense:{key}");
            found = await office.PostAsync("/api/office/expenses", body);
            Log.New($"office expense {label}: {found.Str("status")}");
        }

        var expense = await office.GetAsync($"/api/office/expenses/{found.Id()}");
        expense = expense["expense"] ?? expense;
        if (review == "approve" && expense.Str("status") == "Submitted")
        {
            expense = await As(Cast.Accountant).PostAsync($"/api/office/expenses/{expense.Id()}/approve", new { note = $"{Marker} receipt matches", rowVersion = expense.RowVersion() });
            expense = expense["expense"] ?? expense;
            Log.New($"office expense {label}: Submitted -> {expense.Str("status")}");
        }
        else
        {
            Log.Kept($"office expense {label}: {expense.Str("status")}");
        }
    }

    private async Task<int> UploadOfficeAsync(Session session, string key, string purpose)
    {
        var attachment = await session.UploadAsync("/api/office/attachments", new Dictionary<string, string>
        {
            ["purpose"] = purpose,
            ["idempotencyKey"] = Key($"office-file:{key}"),
            ["captureSource"] = "Camera",
            ["capturedAt"] = today.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc).ToString("O"),
        }, $"DEMO-{key}.png", DemoPng($"office {purpose} {key}"), "image/png");
        return attachment.Id();
    }
}
