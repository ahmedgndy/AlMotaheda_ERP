using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>
/// The prototype's 16 trades with their default daily rates and work types (spec 6.26), seeded by the first
/// migration with fixed ids. Changing them later needs a new migration; new trades are added through the API.
/// </summary>
internal static class TradeSeed
{
    public static IReadOnlyList<Trade> All { get; } =
    [
        Seed(1, "construction", "عامل", "Labourer", 300m, WorkTypes.General),
        Seed(2, "general", "مساعد", "Helper", 250m, WorkTypes.General),
        Seed(3, "mason", "بنّاء", "Mason", 480m, WorkTypes.Brickwork),
        Seed(4, "steel", "حدّاد مسلح", "Steel fixer", 600m, WorkTypes.SteelFixing),
        Seed(5, "carpenter", "نجّار مسلح", "Formwork carpenter", 550m, WorkTypes.Formwork),
        Seed(6, "tile", "مبلّط", "Tiler", 530m, WorkTypes.Tiling),
        Seed(7, "painter", "نقّاش", "Painter", 430m, WorkTypes.Painting),
        Seed(8, "excavation", "عامل حفر", "Excavation worker", 350m, WorkTypes.Excavation),
        Seed(9, "electrician", "كهربائي", "Electrician", 580m, WorkTypes.Mep),
        Seed(10, "plumber", "سبّاك", "Plumber", 550m, WorkTypes.Mep),
        Seed(11, "waterproofing", "فني عزل", "Waterproofing technician", 600m, WorkTypes.Waterproofing),
        Seed(12, "piles", "عامل خوازيق", "Piles worker", 530m, WorkTypes.Piling),
        Seed(13, "security", "غفير", "Guard", 450m, WorkTypes.Guarding),
        Seed(14, "plasterer", "مبيّض محارة", "Plasterer", 480m, WorkTypes.Plastering),
        Seed(15, "operator", "مشغّل معدات", "Equipment operator", 650m, WorkTypes.EquipmentOperation),
        Seed(16, "other", "أخرى", "Other", 550m, WorkTypes.General),
    ];

    private static Trade Seed(int id, string code, string nameAr, string nameEn, decimal rate, string workType) => new()
    {
        Id = id,
        Code = code,
        NameAr = nameAr,
        NameEn = nameEn,
        DefaultDailyRate = rate,
        DefaultWorkType = workType,
        IsActive = true,
    };
}
