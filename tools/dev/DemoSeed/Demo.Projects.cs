using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

/// <summary>A demo project and what the later steps need from it.</summary>
internal sealed record ProjectSpec(
    string Code,
    string NameAr,
    string NameEn,
    string ClientAr,
    string ClientEn,
    string LocationAr,
    string LocationEn,
    double Latitude,
    double Longitude,
    int StartDaysAgo,
    int EndInDays,
    string Status,
    Person? SiteEngineer);

/// <summary>The demo projects (their own class, so other static demo data can refer to them safely).</summary>
internal static class Sites
{
    // Site coordinates are the project's configured site centre (typed by the office), not GPS observations.
    public static readonly ProjectSpec NileTower = new("NT-2026-01", "برج النيل السكني", "Nile Residential Tower",
        "شركة النيل للتطوير العقاري (عميل تجريبي)", "Nile Real Estate Development (demo client)",
        "العاصمة الإدارية الجديدة - الحي السكني R7", "New Administrative Capital, R7 district",
        30.0195, 31.7590, 120, 300, "Active", Cast.Engineer);

    public static readonly ProjectSpec ShoroukSchools = new("SH-2026-02", "مجمع مدارس الشروق", "Shorouk Schools Complex",
        "مدارس المستقبل الخاصة (عميل تجريبي)", "Future Private Schools (demo client)",
        "مدينة الشروق - الحي التاسع", "El Shorouk City, 9th district",
        30.1436, 31.6267, 75, 240, "Active", Cast.Engineer2);

    public static readonly ProjectSpec MaadiVilla = new("MV-2025-07", "فيلا المعادي", "Maadi Villa",
        "عميل فردي (تجريبي)", "Private client (demo)",
        "المعادي - القاهرة", "Maadi, Cairo",
        29.9602, 31.2569, 420, -30, "Completed", null);

    public static readonly ProjectSpec OfficeBuilding = new("OB-2026-03", "مبنى إداري التجمع الخامس", "Fifth Settlement Office Building",
        "شركة الأفق للاستثمار (عميل تجريبي)", "Horizon Investments (demo client)",
        "التجمع الخامس - شارع التسعين", "Fifth Settlement, 90th Street",
        30.0287, 31.4686, -20, 540, "Planning", null);
}

internal sealed partial class Demo
{
    private readonly Dictionary<string, int> _projectIds = new();
    private readonly Dictionary<string, int> _supplierIds = new();
    private readonly Dictionary<string, int> _priceItemIds = new();
    private readonly Dictionary<string, int> _itemIds = new();

    private int ProjectId(ProjectSpec project) => _projectIds[project.Code];

    private async Task PriceListAsync()
    {
        Log.Section("Company price list (Price Control)");
        var ceo = As(Cast.Ceo);
        // (key, ar, en, category, unit, min, max)
        (string Key, string Ar, string En, string Category, string Unit, decimal Min, decimal Max)[] items =
        [
            ("cement", "أسمنت بورتلاندي (شيكارة 50 كجم)", "Portland cement (50 kg bag)", "CementAggregates", "Bag", 220, 250),
            ("rebar", "حديد تسليح", "Reinforcing steel", "Steel", "Ton", 38000, 41000),
            ("readymix", "خرسانة جاهزة إجهاد 350", "Ready-mix concrete C35", "ReadyMix", "M3", 2400, 2700),
            ("bricks", "طوب أحمر", "Red clay bricks", "Masonry", "Brick", 2.2m, 2.8m),
            ("diesel", "سولار", "Diesel", "Fuel", "Liter", 13.5m, 15.5m),
            ("petrol", "بنزين 92", "Petrol 92", "Fuel", "Liter", 15, 17.5m),
            ("internet", "اشتراك إنترنت المكتب", "Office internet subscription", "Internet", "Subscription", 1200, 1600),
            ("paper", "ورق تصوير A4 (رزمة)", "A4 copy paper (ream)", "OfficeExpenses", "Piece", 180, 230),
            ("meal", "وجبة عامل", "Worker meal", "Meals", "Meal", 60, 90),
        ];

        foreach (var item in items)
        {
            var found = (await ceo.GetAsync($"/api/price-items?search={Uri.EscapeDataString(item.En)}&pageSize=10"))
                .Items().FirstOrDefault(row => row.Str("nameEn") == item.En);
            if (found is null)
            {
                // An Executive's new item starts with an approved range (T16).
                found = await ceo.PostAsync("/api/price-items", new
                {
                    nameAr = item.Ar,
                    nameEn = item.En,
                    classification = "Material",
                    unit = item.Unit,
                    categoryCode = item.Category,
                    kind = "Range",
                    minPrice = item.Min,
                    maxPrice = item.Max,
                    currency = "EGP",
                    effectiveFrom = Iso(today.AddDays(-150)),
                    sourceNote = $"{Marker} market survey for the demo",
                    notes = $"{Marker} demo price item",
                });
                found = found["item"]!;
                Log.New($"price item {item.En} {item.Min:N2}–{item.Max:N2} / {item.Unit}");
            }
            else
            {
                Log.Kept($"price item {item.En}");
            }

            _priceItemIds[item.Key] = found.Id();
        }
    }

    private async Task SuppliersAsync()
    {
        Log.Section("Suppliers");
        var ceo = As(Cast.Ceo);
        (string Key, string Ar, string En, string Kind, string Phone)[] suppliers =
        [
            ("cement", "مصنع الوادي للأسمنت (تجريبي)", "Wadi Cement Works (demo)", "Materials", "0220000001"),
            ("steel", "الشركة المتحدة للحديد (تجريبي)", "United Steel Trading (demo)", "Materials", "0220000002"),
            ("readymix", "خرسانة الدلتا الجاهزة (تجريبي)", "Delta Ready-Mix (demo)", "Materials", "0220000003"),
            ("bricks", "مصنع طوب الصعيد (تجريبي)", "Upper Egypt Bricks (demo)", "Materials", "0220000004"),
            ("fuel", "محطة وقود الطريق الدائري (تجريبي)", "Ring Road Fuel Station (demo)", "Fuel", "0220000005"),
            ("equipment", "النصر لتأجير المعدات (تجريبي)", "El Nasr Equipment Rental (demo)", "Equipment", "0220000006"),
        ];

        foreach (var supplier in suppliers)
        {
            var found = (await ceo.GetAsync($"/api/suppliers?search={Uri.EscapeDataString(supplier.En)}&pageSize=10"))
                .Items().FirstOrDefault(row => row.Str("nameEn") == supplier.En);
            if (found is null)
            {
                found = await ceo.PostAsync("/api/suppliers", new
                {
                    nameAr = supplier.Ar,
                    nameEn = supplier.En,
                    kind = supplier.Kind,
                    phone = supplier.Phone,
                    notes = $"{Marker} demo supplier",
                    status = "Active",
                });
                Log.New($"supplier {supplier.En}");
            }
            else
            {
                Log.Kept($"supplier {supplier.En}");
            }

            _supplierIds[supplier.Key] = found.Id();
        }
    }

    private async Task ProjectsAsync()
    {
        Log.Section("Projects and teams");
        var ceo = As(Cast.Ceo);
        foreach (var spec in new[] { Sites.NileTower, Sites.ShoroukSchools, Sites.MaadiVilla, Sites.OfficeBuilding })
        {
            var found = (await ceo.GetAsync($"/api/projects?search={Uri.EscapeDataString(spec.Code)}&pageSize=10"))
                .Items().FirstOrDefault(row => row.Str("code") == spec.Code);
            if (found is null)
            {
                found = await ceo.PostAsync("/api/projects", new
                {
                    code = spec.Code,
                    nameAr = spec.NameAr,
                    nameEn = spec.NameEn,
                    clientNameAr = spec.ClientAr,
                    clientNameEn = spec.ClientEn,
                    locationAr = spec.LocationAr,
                    locationEn = spec.LocationEn,
                    contractNo = $"C-{spec.Code}",
                    siteLatitude = spec.Latitude,
                    siteLongitude = spec.Longitude,
                    siteRadiusMeters = 250,
                    startDate = Iso(today.AddDays(-spec.StartDaysAgo)),
                    endDate = Iso(today.AddDays(spec.EndInDays)),
                    status = spec.Status,
                    notes = $"{Marker} demo project for development testing",
                    managerUserId = UserId(Cast.Manager),
                    siteEngineerUserIds = spec.SiteEngineer is null ? Array.Empty<int>() : [UserId(spec.SiteEngineer)],
                    teamUserIds = Array.Empty<int>(),
                });
                Log.New($"project {spec.Code} {spec.NameEn} ({spec.Status})");
            }
            else
            {
                Log.Kept($"project {spec.Code} {spec.NameEn}");
            }

            _projectIds[spec.Code] = found.Id();
        }
    }

    /// <summary>The Nile Tower's bill of quantities: main items with sub-items, which every cost later lands on.</summary>
    private async Task ItemsAsync()
    {
        Log.Section("Project items (BOQ)");
        var ceo = As(Cast.Ceo);
        (string Key, string? Parent, string Ar, string En, string Unit, string Category)[] items =
        [
            ("concrete", null, "الأعمال الخرسانية", "Concrete works", "M3", "Concrete"),
            ("foundations", "concrete", "خرسانة الأساسات", "Foundation concrete", "M3", "Concrete"),
            ("columns", "concrete", "خرسانة الأعمدة", "Column concrete", "M3", "Concrete"),
            ("slabs", "concrete", "خرسانة الأسقف", "Slab concrete", "M3", "Concrete"),
            ("masonry", null, "أعمال المباني", "Masonry works", "M2", "Brickwork"),
            ("waterproofing", null, "أعمال العزل", "Waterproofing", "M2", "Waterproofing"),
            ("finishing", null, "أعمال التشطيبات", "Finishing works", "M2", "Plastering"),
            ("site", null, "مصروفات الموقع العامة", "General site costs", "Lot", "General"),
        ];

        await AddItemsAsync(ceo, Sites.NileTower, items, "nt");
        await AddItemsAsync(ceo, Sites.ShoroukSchools,
        [
            ("concrete", null, "الأعمال الخرسانية", "Concrete works", "M3", "Concrete"),
            ("masonry", null, "أعمال المباني", "Masonry works", "M2", "Brickwork"),
            ("site", null, "مصروفات الموقع العامة", "General site costs", "Lot", "General"),
        ], "sh");
    }

    private async Task AddItemsAsync(Session session, ProjectSpec project, (string Key, string? Parent, string Ar, string En, string Unit, string Category)[] items, string prefix)
    {
        var existing = List(await session.GetAsync($"/api/projects/{ProjectId(project)}/items?includeInactive=true")).ToList();
        IEnumerable<JsonNode> Flatten(IEnumerable<JsonNode> nodes) =>
            nodes.SelectMany(node => new[] { node }.Concat(Flatten(node["subItems"] as JsonArray is { } children ? children.Where(child => child is not null).Select(child => child!) : [])));
        var all = Flatten(existing).ToList();
        foreach (var item in items)
        {
            var found = all.FirstOrDefault(row => row.Str("nameEn") == item.En);
            if (found is null)
            {
                found = await session.PostAsync($"/api/projects/{ProjectId(project)}/items", new
                {
                    nameAr = item.Ar,
                    nameEn = item.En,
                    category = item.Category,
                    unit = item.Unit,
                    notes = Marker,
                    parentItemId = item.Parent is null ? (int?)null : _itemIds[$"{prefix}.{item.Parent}"],
                });
                Log.New($"{project.Code} item {item.En}");
            }
            else
            {
                Log.Kept($"{project.Code} item {item.En}");
            }

            _itemIds[$"{prefix}.{item.Key}"] = found.Id();
        }
    }
}
