using AlMotaheda.Modules.Fleet.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Register;

/// <summary>Where a machine is today: its open assignment.</summary>
internal sealed record OpenAssignmentRef(int AssignmentId, int ProjectId, DateOnly StartDate);

/// <summary>A machine's current rental terms.</summary>
internal sealed record CurrentRentalRef(int RentalId, int SupplierId, RateBasis RateBasis, decimal Rate, DateOnly StartDate);

/// <summary>One machine of the register with where it is and its current terms, as the queries project it.</summary>
internal sealed class EquipmentRow
{
    public int Id { get; init; }
    public string AssetNo { get; init; } = "";
    public string NameAr { get; init; } = "";
    public string NameEn { get; init; } = "";
    public EquipmentCategory Category { get; init; }
    public string TypeName { get; init; } = "";
    public string? SerialNo { get; init; }
    public EquipmentOwnership Ownership { get; init; }
    public EquipmentCondition Condition { get; init; }
    public EquipmentStatus Status { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public OpenAssignmentRef? CurrentAssignment { get; init; }
    public CurrentRentalRef? CurrentRental { get; init; }

    /// <summary>In service and on no project: can be sent to a project now.</summary>
    public bool IsAvailable => Status == EquipmentStatus.InService && CurrentAssignment is null;
}

/// <summary>The filters of <c>GET /api/equipment</c>, after validation.</summary>
internal sealed record EquipmentFilter(
    string? Search,
    EquipmentCategory? Category,
    EquipmentOwnership? Ownership,
    EquipmentStatus? Status,
    bool? Available);

/// <summary>The register queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class EquipmentQueries
{
    /// <summary>The register list: every filter, the sort (by asset number) and the paging run in SQL.</summary>
    public static IQueryable<EquipmentRow> Search(FleetDbContext db, EquipmentFilter filter)
    {
        var query = db.Equipment.AsNoTracking();

        if (filter.Search is { } text)
        {
            query = query.Where(equipment =>
                equipment.AssetNo.Contains(text)
                || equipment.NameAr.Contains(text)
                || equipment.NameEn.Contains(text)
                || equipment.TypeName.Contains(text)
                || (equipment.SerialNo != null && equipment.SerialNo.Contains(text)));
        }

        if (filter.Category is { } category)
        {
            query = query.Where(equipment => equipment.Category == category);
        }

        if (filter.Ownership is { } ownership)
        {
            query = query.Where(equipment => equipment.Ownership == ownership);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(equipment => equipment.Status == status);
        }

        // Available = in service and not on a project now.
        if (filter.Available is true)
        {
            query = query.Where(equipment => equipment.Status == EquipmentStatus.InService
                && !db.EquipmentAssignments.Any(assignment => assignment.EquipmentId == equipment.Id && assignment.EndDate == null));
        }
        else if (filter.Available is false)
        {
            query = query.Where(equipment => equipment.Status != EquipmentStatus.InService
                || db.EquipmentAssignments.Any(assignment => assignment.EquipmentId == equipment.Id && assignment.EndDate == null));
        }

        return Project(db, query.OrderBy(equipment => equipment.AssetNo));
    }

    /// <summary>One machine with where it is and its current terms.</summary>
    public static IQueryable<EquipmentRow> One(FleetDbContext db, int equipmentId) =>
        Project(db, db.Equipment.AsNoTracking().Where(equipment => equipment.Id == equipmentId));

    /// <summary>Every set of rental terms of one machine, newest first.</summary>
    public static IQueryable<EquipmentRental> Rentals(FleetDbContext db, int equipmentId) =>
        db.EquipmentRentals.AsNoTracking()
            .Where(rental => rental.EquipmentId == equipmentId)
            .OrderByDescending(rental => rental.StartDate)
            .ThenByDescending(rental => rental.Id);

    private static IQueryable<EquipmentRow> Project(FleetDbContext db, IQueryable<Equipment> query) =>
        query.Select(equipment => new EquipmentRow
        {
            Id = equipment.Id,
            AssetNo = equipment.AssetNo,
            NameAr = equipment.NameAr,
            NameEn = equipment.NameEn,
            Category = equipment.Category,
            TypeName = equipment.TypeName,
            SerialNo = equipment.SerialNo,
            Ownership = equipment.Ownership,
            Condition = equipment.Condition,
            Status = equipment.Status,
            Notes = equipment.Notes,
            CreatedAt = equipment.CreatedAt,
            CreatedBy = equipment.CreatedBy,
            UpdatedAt = equipment.UpdatedAt,
            UpdatedBy = equipment.UpdatedBy,
            RowVersion = equipment.RowVersion,
            CurrentAssignment = db.EquipmentAssignments
                .Where(assignment => assignment.EquipmentId == equipment.Id && assignment.EndDate == null)
                .Select(assignment => new OpenAssignmentRef(assignment.Id, assignment.ProjectId, assignment.StartDate))
                .FirstOrDefault(),
            CurrentRental = db.EquipmentRentals
                .Where(rental => rental.EquipmentId == equipment.Id && rental.EndDate == null)
                .Select(rental => new CurrentRentalRef(rental.Id, rental.SupplierId, rental.RateBasis, rental.Rate, rental.StartDate))
                .FirstOrDefault(),
        });
}
