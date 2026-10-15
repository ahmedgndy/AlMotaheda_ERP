using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Assignments;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Fleet.Features.Register;

/// <summary>One set of rental terms. <see cref="Rate"/> is <c>null</c> without equipment See money.</summary>
internal sealed record EquipmentRentalView(
    int Id,
    int SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    string RateBasis,
    decimal? Rate,
    DateOnly StartDate,
    DateOnly? EndDate,
    bool IsCurrent,
    string? Notes)
{
    public static EquipmentRentalView From(EquipmentRental rental, IReadOnlyDictionary<int, SupplierRef> suppliers, bool seesMoney)
    {
        suppliers.TryGetValue(rental.SupplierId, out var supplier);
        return new EquipmentRentalView(
            rental.Id,
            rental.SupplierId,
            supplier?.NameAr,
            supplier?.NameEn,
            rental.RateBasis.ToString(),
            seesMoney ? rental.Rate : null,
            rental.StartDate,
            rental.EndDate,
            rental.EndDate is null,
            rental.Notes);
    }

    public static EquipmentRentalView From(CurrentRentalRef rental, IReadOnlyDictionary<int, SupplierRef> suppliers, bool seesMoney)
    {
        suppliers.TryGetValue(rental.SupplierId, out var supplier);
        return new EquipmentRentalView(
            rental.RentalId,
            rental.SupplierId,
            supplier?.NameAr,
            supplier?.NameEn,
            rental.RateBasis.ToString(),
            seesMoney ? rental.Rate : null,
            rental.StartDate,
            null,
            true,
            null);
    }
}

/// <summary>
/// One machine on the register list. <see cref="CurrentProjectId"/> is where it is now; the frontend shows the
/// project's name from its own project list, which holds only projects the caller may see.
/// </summary>
internal sealed record EquipmentListItem(
    int Id,
    string AssetNo,
    string NameAr,
    string NameEn,
    string Category,
    string TypeName,
    string? SerialNo,
    string Ownership,
    string Condition,
    string Status,
    bool IsAvailable,
    int? CurrentProjectId,
    DateOnly? OnProjectSince,
    EquipmentRentalView? CurrentRental)
{
    public static EquipmentListItem From(EquipmentRow row, IReadOnlyDictionary<int, SupplierRef> suppliers, bool seesMoney) =>
        new(
            row.Id,
            row.AssetNo,
            row.NameAr,
            row.NameEn,
            row.Category.ToString(),
            row.TypeName,
            row.SerialNo,
            row.Ownership.ToString(),
            row.Condition.ToString(),
            row.Status.ToString(),
            row.IsAvailable,
            row.CurrentAssignment?.ProjectId,
            row.CurrentAssignment?.StartDate,
            row.CurrentRental is { } rental ? EquipmentRentalView.From(rental, suppliers, seesMoney) : null);
}

/// <summary>
/// One machine with its rental terms over time and its assignment history, as <c>GET /api/equipment/{id}</c>,
/// register, edit and new terms return it.
/// </summary>
/// <param name="IsAssigned">True when the machine is on a project now, even one the caller may not see.</param>
/// <param name="CurrentAssignment">Where it is now, when the caller may see that project.</param>
/// <param name="History">Every stay on a project the caller may see, newest first, the current one included.</param>
/// <param name="HiddenHistoryCount">Stays on projects the caller may not see: counted, not shown.</param>
internal sealed record EquipmentDetails(
    int Id,
    string AssetNo,
    string NameAr,
    string NameEn,
    string Category,
    string TypeName,
    string? SerialNo,
    string Ownership,
    string Condition,
    string Status,
    string? Notes,
    bool IsAvailable,
    bool IsAssigned,
    EquipmentRentalView? CurrentRental,
    IReadOnlyList<EquipmentRentalView> Rentals,
    EquipmentAssignmentView? CurrentAssignment,
    IReadOnlyList<EquipmentAssignmentView> History,
    int HiddenHistoryCount,
    DateTime CreatedAt,
    FleetPerson CreatedBy,
    DateTime? UpdatedAt,
    FleetPerson? UpdatedBy,
    string RowVersion);
