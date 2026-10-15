using System.Text.RegularExpressions;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;

namespace AlMotaheda.Modules.Fleet.Features.Register;

/// <summary>The rental terms of a rented machine, as sent on register, on edit and on new terms.</summary>
internal sealed record RentalTermsRequest(int? SupplierId, string? RateBasis, decimal? Rate, string? StartDate, string? Notes);

/// <summary>Rental terms after validation.</summary>
internal sealed record RentalTermsFields(int SupplierId, RateBasis RateBasis, decimal Rate, DateOnly StartDate, string? Notes)
{
    /// <param name="prefix">Prefix of the field names in errors: <c>"rental."</c> when the terms are nested in a form.</param>
    public static RentalTermsFields? Validate(RentalTermsRequest request, ValidationErrors errors, string prefix = "")
    {
        var before = errors.FieldCount;

        if (request.SupplierId is null or < 1)
        {
            errors.Add(prefix + "supplierId", "The lessor (supplierId) is required.");
        }

        var basis = Input.RequiredName<RateBasis>(request.RateBasis, errors, prefix + "rateBasis");
        var rate = NumberInput.RequiredMoney(request.Rate, errors, prefix + "rate", "The rate");
        var startDate = Input.RequiredDate(request.StartDate, errors, prefix + "startDate", "The start date");
        var notes = Input.OptionalText(request.Notes, EquipmentRentalConfiguration.NotesMaxLength, errors, prefix + "notes", "Notes");

        return errors.FieldCount > before
            ? null
            : new RentalTermsFields(request.SupplierId!.Value, basis!.Value, rate!.Value, startDate!.Value, notes);
    }

    public EquipmentRental ToRow(int equipmentId, DateTime now, int userId) => new()
    {
        EquipmentId = equipmentId,
        SupplierId = SupplierId,
        RateBasis = RateBasis,
        Rate = Rate,
        StartDate = StartDate,
        Notes = Notes,
        CreatedAt = now,
        CreatedBy = userId,
    };
}

/// <summary>The register form, as sent on create and on edit.</summary>
internal interface IEquipmentFieldsRequest
{
    string? AssetNo { get; }
    string? NameAr { get; }
    string? NameEn { get; }
    string? Category { get; }
    string? TypeName { get; }
    string? SerialNo { get; }
    string? Ownership { get; }
    string? Condition { get; }
    string? Status { get; }
    string? Notes { get; }
    RentalTermsRequest? Rental { get; }
}

/// <summary>The register form after validation. The asset number is stored upper-case.</summary>
internal sealed partial record EquipmentFields(
    string AssetNo,
    string NameAr,
    string NameEn,
    EquipmentCategory Category,
    string TypeName,
    string? SerialNo,
    EquipmentOwnership Ownership,
    EquipmentCondition Condition,
    EquipmentStatus Status,
    string? Notes,
    RentalTermsFields? Rental)
{
    public const int AssetNoMinLength = 2;

    public static EquipmentFields? Validate(IEquipmentFieldsRequest request, ValidationErrors errors)
    {
        var assetNo = ValidateAssetNo(request.AssetNo, errors);
        var nameAr = Input.RequiredText(request.NameAr, EquipmentConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, EquipmentConfiguration.NameMaxLength, errors, "nameEn", "The English name");
        var category = Input.RequiredName<EquipmentCategory>(request.Category, errors, "category");
        var typeName = Input.RequiredText(request.TypeName, EquipmentConfiguration.TypeNameMaxLength, errors, "typeName", "The type");
        var serialNo = Input.OptionalText(request.SerialNo, EquipmentConfiguration.SerialNoMaxLength, errors, "serialNo", "The serial number");
        var ownership = Input.RequiredName<EquipmentOwnership>(request.Ownership, errors, "ownership");
        var condition = Input.RequiredName<EquipmentCondition>(request.Condition, errors, "condition");
        var status = Input.OptionalName<EquipmentStatus>(request.Status, errors, "status") ?? EquipmentStatus.InService;
        var notes = Input.OptionalText(request.Notes, EquipmentConfiguration.NotesMaxLength, errors, "notes", "Notes");

        // Whether rental terms are required depends on the machine's ownership before the change, which only the
        // handler knows; here they are validated whenever they are sent.
        var rental = request.Rental is null ? null : RentalTermsFields.Validate(request.Rental, errors, "rental.");

        if (ownership == EquipmentOwnership.Owned && request.Rental is not null)
        {
            errors.Add("rental", "Rental terms apply to rented equipment only.");
        }

        if (errors.HasErrors)
        {
            return null;
        }

        return new EquipmentFields(
            assetNo!, nameAr!, nameEn!, category!.Value, typeName!, serialNo, ownership!.Value, condition!.Value, status, notes, rental);
    }

    public void ApplyTo(Equipment equipment)
    {
        equipment.AssetNo = AssetNo;
        equipment.NameAr = NameAr;
        equipment.NameEn = NameEn;
        equipment.Category = Category;
        equipment.TypeName = TypeName;
        equipment.SerialNo = SerialNo;
        equipment.Ownership = Ownership;
        equipment.Condition = Condition;
        equipment.Status = Status;
        equipment.Notes = Notes;
    }

    /// <summary>The names of the fields that differ, for the audit log (never their values).</summary>
    public string ChangedFields(Equipment equipment)
    {
        var changed = new List<string>();
        if (equipment.AssetNo != AssetNo) changed.Add("assetNo");
        if (equipment.NameAr != NameAr) changed.Add("nameAr");
        if (equipment.NameEn != NameEn) changed.Add("nameEn");
        if (equipment.Category != Category) changed.Add("category");
        if (equipment.TypeName != TypeName) changed.Add("typeName");
        if (equipment.SerialNo != SerialNo) changed.Add("serialNo");
        if (equipment.Ownership != Ownership) changed.Add("ownership");
        if (equipment.Condition != Condition) changed.Add("condition");
        if (equipment.Status != Status) changed.Add("status");
        if (equipment.Notes != Notes) changed.Add("notes");
        return changed.Count == 0 ? "none" : string.Join(",", changed);
    }

    private static string? ValidateAssetNo(string? value, ValidationErrors errors)
    {
        var assetNo = Input.Clean(value)?.ToUpperInvariant();
        if (assetNo is null)
        {
            errors.Add("assetNo", "The asset number is required.");
            return null;
        }

        if (assetNo.Length is < AssetNoMinLength or > EquipmentConfiguration.AssetNoMaxLength || !AssetNoPattern().IsMatch(assetNo))
        {
            errors.Add("assetNo",
                $"The asset number must be {AssetNoMinLength} to {EquipmentConfiguration.AssetNoMaxLength} letters or digits, "
                + "in groups joined by single hyphens, for example EX-104.");
            return null;
        }

        return assetNo;
    }

    [GeneratedRegex("^[A-Z0-9]+(-[A-Z0-9]+)*$")]
    private static partial Regex AssetNoPattern();
}

/// <summary>
/// The rules for rental terms over time, with no database access. New terms start after every earlier term, and
/// the open terms end the day before the new ones start.
/// </summary>
internal static class RentalTimeline
{
    /// <summary>
    /// The earliest allowed start for new terms, or <c>null</c> when the machine has no terms yet: the day after the
    /// latest start, and the day after the latest end.
    /// </summary>
    public static DateOnly? EarliestNextStart(IReadOnlyCollection<(DateOnly Start, DateOnly? End)> terms)
    {
        if (terms.Count == 0)
        {
            return null;
        }

        var latest = terms.Max(term => term.End is { } end && end > term.Start ? end : term.Start);
        return latest.AddDays(1);
    }

    /// <summary>The end date given to the open terms when new terms start on <paramref name="newStart"/>.</summary>
    public static DateOnly CloseBefore(DateOnly newStart) => newStart.AddDays(-1);

    /// <summary>
    /// The end date given to the open terms when the machine stops being rented: today, or the start of the terms
    /// when they had not started yet (an end before the start is not a valid period).
    /// </summary>
    public static DateOnly CloseOn(DateOnly openStart, DateOnly today) => today < openStart ? openStart : today;
}
