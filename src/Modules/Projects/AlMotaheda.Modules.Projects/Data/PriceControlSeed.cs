using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// The first categories and units of the price list, seeded by the migration with fixed ids. The categories start
/// with the project catalog's classifications (so existing price items keep theirs) and add the company's operating
/// expenses; the units start with the shared measurement units every record uses. Later ones are added through the
/// API; changing a seeded row later needs a new migration.
/// </summary>
internal static class PriceControlSeed
{
    private static readonly DateTime SeededAt = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<PriceCategory> Categories { get; } =
    [
        Category(1, nameof(CatalogCategory.CementAggregates), "أسمنت وركام", "Cement and aggregates"),
        Category(2, nameof(CatalogCategory.ReadyMix), "خرسانة جاهزة", "Ready-mix concrete"),
        Category(3, nameof(CatalogCategory.Steel), "حديد", "Steel"),
        Category(4, nameof(CatalogCategory.Masonry), "مباني", "Masonry"),
        Category(5, nameof(CatalogCategory.Finishing), "تشطيبات", "Finishing"),
        Category(6, nameof(CatalogCategory.Waterproofing), "عزل", "Waterproofing"),
        Category(7, nameof(CatalogCategory.Piling), "خوازيق", "Piling"),
        Category(8, nameof(CatalogCategory.Formwork), "شدات", "Formwork"),
        Category(9, nameof(CatalogCategory.Consumables), "مستهلكات", "Consumables"),
        Category(10, nameof(CatalogCategory.Services), "خدمات", "Services"),
        Category(11, nameof(CatalogCategory.Other), "أخرى", "Other"),
        Category(12, "Meals", "وجبات العمال", "Worker meals and food"),
        Category(13, "Fuel", "وقود", "Fuel"),
        Category(14, "Internet", "اشتراكات الإنترنت", "Internet subscriptions"),
        Category(15, "OfficeExpenses", "مصروفات مكتبية", "Office expenses"),
        Category(16, "Maintenance", "صيانة", "Maintenance"),
        Category(17, "Transport", "نقل", "Transport"),
        Category(18, "EquipmentRental", "تأجير معدات", "Equipment rental"),
    ];

    public static IReadOnlyList<PriceUnit> Units { get; } =
    [
        Unit(1, MeasurementUnits.Bag, "شيكارة", "Bag"),
        Unit(2, MeasurementUnits.Ton, "طن", "Ton"),
        Unit(3, MeasurementUnits.M3, "م³", "Cubic meter"),
        Unit(4, MeasurementUnits.M2, "م²", "Square meter"),
        Unit(5, MeasurementUnits.M, "متر", "Meter"),
        Unit(6, MeasurementUnits.Piece, "قطعة", "Piece"),
        Unit(7, MeasurementUnits.Trip, "نقلة", "Trip"),
        Unit(8, MeasurementUnits.Roll, "لفة", "Roll"),
        Unit(9, MeasurementUnits.Lot, "مقطوعية", "Lot"),
        Unit(10, MeasurementUnits.Job, "عملية", "Job"),
        Unit(11, MeasurementUnits.Day, "يوم", "Day"),
        Unit(12, MeasurementUnits.Visit, "زيارة", "Visit"),
        Unit(13, MeasurementUnits.Brick, "طوبة", "Brick"),
        Unit(14, MeasurementUnits.Liter, "لتر", "Liter"),
        Unit(15, MeasurementUnits.Kg, "كجم", "Kilogram"),
        Unit(16, MeasurementUnits.Other, "أخرى", "Other"),
        Unit(17, "WorkerDay", "عامل/يوم", "Worker per day"),
        Unit(18, "Meal", "وجبة", "Meal"),
        Unit(19, "Month", "شهر", "Month"),
        Unit(20, "Subscription", "اشتراك شهري", "Monthly subscription"),
        Unit(21, "Hour", "ساعة", "Hour"),
        Unit(22, "Km", "كم", "Kilometer"),
    ];

    private static PriceCategory Category(int id, string code, string nameAr, string nameEn) =>
        new() { Id = id, Code = code, NameAr = nameAr, NameEn = nameEn, IsActive = true, SortOrder = id, UpdatedAt = SeededAt };

    private static PriceUnit Unit(int id, string code, string nameAr, string nameEn) =>
        new() { Id = id, Code = code, NameAr = nameAr, NameEn = nameEn, IsActive = true, SortOrder = id, UpdatedAt = SeededAt };
}
