namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// Units of measurement, shared by every module so "م²" means the same thing on a project item, a supply, a
/// contract item and a daily report. Stored and returned by these names.
/// </summary>
public static class MeasurementUnits
{
    public const string Bag = "Bag";
    public const string Ton = "Ton";
    public const string M3 = "M3";
    public const string M2 = "M2";
    public const string M = "M";
    public const string Piece = "Piece";
    public const string Trip = "Trip";
    public const string Roll = "Roll";
    public const string Lot = "Lot";
    public const string Job = "Job";
    public const string Day = "Day";
    public const string Visit = "Visit";
    public const string Brick = "Brick";
    public const string Liter = "Liter";
    public const string Kg = "Kg";
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } =
        [Bag, Ton, M3, M2, M, Piece, Trip, Roll, Lot, Job, Day, Visit, Brick, Liter, Kg, Other];

    /// <summary>Units measured as a part of one whole (a lump sum), where progress is a percentage.</summary>
    public static bool IsLumpSum(string unit) => unit is Lot or Job;

    public static bool TryParse(string? value, out string unit) => CodeList.TryParse(All, value, out unit);
}

/// <summary>
/// Kinds of construction work. A project item's category, a labor line's work type and a contract item's work
/// type use the same list, so company labor and subcontract work on the same kind of work can be compared.
/// </summary>
public static class WorkTypes
{
    public const string Concrete = "Concrete";
    public const string SteelFixing = "SteelFixing";
    public const string Formwork = "Formwork";
    public const string Brickwork = "Brickwork";
    public const string Plastering = "Plastering";
    public const string Tiling = "Tiling";
    public const string Painting = "Painting";
    public const string Excavation = "Excavation";
    public const string Waterproofing = "Waterproofing";
    public const string Piling = "Piling";
    public const string Mep = "Mep";
    public const string Guarding = "Guarding";
    public const string EquipmentOperation = "EquipmentOperation";
    public const string General = "General";

    public static IReadOnlyList<string> All { get; } =
    [
        Concrete, SteelFixing, Formwork, Brickwork, Plastering, Tiling, Painting, Excavation, Waterproofing,
        Piling, Mep, Guarding, EquipmentOperation, General,
    ];

    public static bool TryParse(string? value, out string workType) => CodeList.TryParse(All, value, out workType);
}

/// <summary>The cost groups of a project: budget lines and cost reports are broken down by these.</summary>
public static class CostGroups
{
    public const string Labor = "Labor";
    public const string Subcontractors = "Subcontractors";
    public const string Materials = "Materials";
    public const string Equipment = "Equipment";
    public const string SiteExpenses = "SiteExpenses";
    public const string Other = "Other";

    /// <summary>Engineers' and employees' salaries allocated to the project by approved working days (HR).</summary>
    public const string Staff = "Staff";

    public static IReadOnlyList<string> All { get; } = [Labor, Subcontractors, Materials, Equipment, SiteExpenses, Other, Staff];

    public static bool TryParse(string? value, out string group) => CodeList.TryParse(All, value, out group);
}

/// <summary>How much of a recognized amount has been paid. Always calculated, never stored.</summary>
public static class PaymentStates
{
    public const string Unpaid = "Unpaid";
    public const string PartiallyPaid = "PartiallyPaid";
    public const string Paid = "Paid";

    public static IReadOnlyList<string> All { get; } = [Unpaid, PartiallyPaid, Paid];

    /// <summary>The state of a recognized <paramref name="amount"/> of which <paramref name="paid"/> is settled.</summary>
    public static string Of(decimal amount, decimal paid) =>
        paid <= 0 ? Unpaid
        : paid >= amount ? Paid
        : PartiallyPaid;

    public static bool TryParse(string? value, out string state) => CodeList.TryParse(All, value, out state);
}

/// <summary>The one rounding rule for money: two decimals, halves away from zero.</summary>
public static class Money
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

internal static class CodeList
{
    /// <summary>Finds <paramref name="value"/> in the list ignoring case and returns the list's spelling.</summary>
    public static bool TryParse(IReadOnlyList<string> all, string? value, out string code)
    {
        var text = value?.Trim();
        foreach (var candidate in all)
        {
            if (string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
            {
                code = candidate;
                return true;
            }
        }

        code = "";
        return false;
    }
}
