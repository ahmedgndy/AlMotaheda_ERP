namespace AlMotaheda.DemoSeed;

internal sealed partial class Demo
{
    /// <summary>
    /// Material deliveries on supplier credit (the supplier invoices, Finance pays later) and site expenses paid from the
    /// Shorouk custody. The engineer records; the Accountant reviews (projects Approve, never the recorder). The bricks
    /// are above their reference range: the server opens a price exception that waits for the Executive.
    /// </summary>
    private async Task ExpensesAsync()
    {
        Log.Section("Material deliveries and site expenses");

        // Nile Tower supplies on credit.
        await ExpenseAsync("nt-cement-1", Sites.NileTower, Cast.Engineer, new
        {
            kind = "Supply", expenseDate = Iso(today.AddDays(-30)), supplierId = _supplierIds["cement"], itemName = "أسمنت بورتلاندي",
            unit = "Bag", quantity = 400, unitPrice = 235m, channel = "Credit", invoiceNo = "WC-INV-1042", deliveryNoteNo = "WC-DN-5531",
            projectItemId = _itemIds["nt.foundations"], priceItemId = _priceItemIds["cement"],
        }, "400 bags of cement (credit)", review: "approve");

        await ExpenseAsync("nt-rebar-1", Sites.NileTower, Cast.Engineer, new
        {
            kind = "Supply", expenseDate = Iso(today.AddDays(-28)), supplierId = _supplierIds["steel"], itemName = "حديد تسليح 16 مم",
            unit = "Ton", quantity = 12, unitPrice = 39500m, channel = "Credit", invoiceNo = "UST-2231", deliveryNoteNo = "UST-DN-778",
            projectItemId = _itemIds["nt.columns"], priceItemId = _priceItemIds["rebar"],
        }, "12 t of rebar (credit)", review: "approve");

        await ExpenseAsync("nt-readymix-1", Sites.NileTower, Cast.Engineer, new
        {
            kind = "Supply", expenseDate = Iso(today.AddDays(-21)), supplierId = _supplierIds["readymix"], itemName = "خرسانة جاهزة إجهاد 350",
            unit = "M3", quantity = 60, unitPrice = 2550m, channel = "Credit", invoiceNo = "DRM-9912", deliveryNoteNo = "DRM-DN-4410",
            projectItemId = _itemIds["nt.slabs"], priceItemId = _priceItemIds["readymix"],
        }, "60 m3 of ready-mix (credit)", review: "approve");

        await ExpenseAsync("nt-bricks-above-range", Sites.NileTower, Cast.Engineer, new
        {
            kind = "Supply", expenseDate = Iso(today.AddDays(-6)), supplierId = _supplierIds["bricks"], itemName = "طوب أحمر",
            unit = "Brick", quantity = 20000, unitPrice = 3.10m, channel = "Credit", invoiceNo = "UEB-0312", deliveryNoteNo = "UEB-DN-071",
            projectItemId = _itemIds["nt.masonry"], priceItemId = _priceItemIds["bricks"],
            priceJustification = "نقص في المعروض بعد توقف المصانع القريبة؛ السعر المتاح الوحيد بالكمية المطلوبة / Shortage after nearby kilns stopped; the only price for this quantity",
        }, "20,000 bricks above the reference range (waits for the Executive)", review: null);

        await ExpenseAsync("nt-cement-2", Sites.NileTower, Cast.Engineer, new
        {
            kind = "Supply", expenseDate = Iso(today.AddDays(-2)), supplierId = _supplierIds["cement"], itemName = "أسمنت بورتلاندي",
            unit = "Bag", quantity = 200, unitPrice = 240m, channel = "Credit", invoiceNo = "WC-INV-1077", deliveryNoteNo = "WC-DN-5602",
            projectItemId = _itemIds["nt.masonry"], priceItemId = _priceItemIds["cement"],
        }, "200 bags of cement waiting for review", review: null);

        // Shorouk site expenses paid from Youssef's custody.
        await ExpenseAsync("sh-transport", Sites.ShoroukSchools, Cast.Engineer2, new
        {
            kind = "Expense", expenseDate = Iso(today.AddDays(-9)), category = "FeesTransport", description = "نقل معدات صغيرة من المخزن للموقع / Small tools from the store to the site",
            amount = 1500m, channel = "Custody", projectItemId = _itemIds["sh.site"], payeeName = "نقل الأمانة (تجريبي)",
        }, "transport 1,500 from custody", review: "approve");

        await ExpenseAsync("sh-meals", Sites.ShoroukSchools, Cast.Engineer2, new
        {
            kind = "Supply", expenseDate = Iso(today.AddDays(-4)), itemName = "وجبات العمال", unit = "Piece", quantity = 30, unitPrice = 75m,
            // Project supplies take the fixed unit list (no "Meal"), so the meal price item can't be linked here.
            channel = "Custody", projectItemId = _itemIds["sh.site"], payeeName = "مطعم الشروق (تجريبي)",
        }, "30 worker meals from custody", review: "approve");

        await ExpenseAsync("sh-consumables", Sites.ShoroukSchools, Cast.Engineer2, new
        {
            kind = "Expense", expenseDate = Iso(today.AddDays(-1)), category = "Consumables", description = "مسامير وأسلاك رباط وأدوات قطع / Nails, binding wire and cutting discs",
            amount = 800m, channel = "Custody", projectItemId = _itemIds["sh.concrete"], payeeName = "حدايد الشروق (تجريبي)",
        }, "consumables 800 waiting for review", review: null);

        await ExpenseAsync("sh-lab", Sites.ShoroukSchools, Cast.Engineer2, new
        {
            kind = "Expense", expenseDate = Iso(today.AddDays(-5)), category = "Lab", description = "اختبار مكعبات خرسانة / Concrete cube tests",
            amount = 2200m, channel = "Custody", projectItemId = _itemIds["sh.concrete"], payeeName = "معمل الجودة (تجريبي)",
        }, "lab tests returned for the invoice", review: "return");
    }

    private async Task ExpenseAsync(string key, ProjectSpec project, Person recorder, object body, string label, string? review)
    {
        var path = $"/api/projects/{ProjectId(project)}/expenses";
        var fields = System.Text.Json.JsonSerializer.SerializeToNode(body)!.AsObject();
        fields["notes"] = $"{Marker} {key}";
        fields["idempotencyKey"] = Key($"expense:{key}");
        var expense = await As(recorder).PostAsync(path, fields);
        expense = expense["expense"] ?? expense;
        var start = expense.Str("status");
        if (start == "Recorded" && review is not null)
        {
            var accountant = As(Cast.Accountant);
            expense = review switch
            {
                "approve" => await accountant.PostAsync($"{path}/{expense.Id()}/approve", new { note = $"{Marker} invoice and delivery note checked", rowVersion = expense.RowVersion() }),
                _ => await accountant.PostAsync($"{path}/{expense.Id()}/return", new { note = "برجاء إرفاق فاتورة المعمل الأصلية / Attach the lab's original invoice.", rowVersion = expense.RowVersion() }),
            };
            expense = expense["expense"] ?? expense;
            Log.New($"{project.Code} {label}: {start} -> {expense.Str("status")}");
        }
        else
        {
            Log.Kept($"{project.Code} {label}: {start}");
        }
    }
}
