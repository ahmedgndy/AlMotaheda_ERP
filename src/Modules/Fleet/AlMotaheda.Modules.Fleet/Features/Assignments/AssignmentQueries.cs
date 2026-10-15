using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Assignments;

/// <summary>One assignment with the machine next to it, as the queries project it.</summary>
internal sealed class AssignmentRow
{
    public int Id { get; init; }
    public int EquipmentId { get; init; }
    public string AssetNo { get; init; } = "";
    public string EquipmentNameAr { get; init; } = "";
    public string EquipmentNameEn { get; init; } = "";
    public EquipmentCategory Category { get; init; }
    public string TypeName { get; init; } = "";
    public EquipmentOwnership Ownership { get; init; }
    public EquipmentCondition Condition { get; init; }
    public EquipmentStatus EquipmentStatus { get; init; }
    public int ProjectId { get; init; }
    public int? ResponsibleUserId { get; init; }
    public string? OperatorName { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? Notes { get; init; }
    public DateTime AssignedAt { get; init; }
    public int AssignedBy { get; init; }
    public DateTime? EndedAt { get; init; }
    public int? EndedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

/// <summary>The assignment queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class AssignmentQueries
{
    /// <summary>
    /// The machines of one project: on it now, or with <paramref name="includeEnded"/> every stay it ever had.
    /// Current stays first, then the newest.
    /// </summary>
    public static IQueryable<AssignmentRow> OfProject(FleetDbContext db, int projectId, bool includeEnded)
    {
        var query = db.EquipmentAssignments.AsNoTracking().Where(assignment => assignment.ProjectId == projectId);
        if (!includeEnded)
        {
            query = query.Where(assignment => assignment.EndDate == null);
        }

        return Project(query
            .OrderBy(assignment => assignment.EndDate == null ? 0 : 1)
            .ThenByDescending(assignment => assignment.StartDate)
            .ThenByDescending(assignment => assignment.Id));
    }

    /// <summary>Every stay of one machine on any project, newest first: its history.</summary>
    public static IQueryable<AssignmentRow> OfEquipment(FleetDbContext db, int equipmentId) =>
        Project(db.EquipmentAssignments.AsNoTracking()
            .Where(assignment => assignment.EquipmentId == equipmentId)
            .OrderByDescending(assignment => assignment.StartDate)
            .ThenByDescending(assignment => assignment.Id));

    /// <summary>One assignment of a project, or none when the id belongs to another project.</summary>
    public static IQueryable<AssignmentRow> One(FleetDbContext db, int projectId, int assignmentId) =>
        Project(db.EquipmentAssignments.AsNoTracking()
            .Where(assignment => assignment.ProjectId == projectId && assignment.Id == assignmentId));

    private static IQueryable<AssignmentRow> Project(IQueryable<EquipmentAssignment> query) =>
        query.Select(assignment => new AssignmentRow
        {
            Id = assignment.Id,
            EquipmentId = assignment.EquipmentId,
            AssetNo = assignment.Equipment.AssetNo,
            EquipmentNameAr = assignment.Equipment.NameAr,
            EquipmentNameEn = assignment.Equipment.NameEn,
            Category = assignment.Equipment.Category,
            TypeName = assignment.Equipment.TypeName,
            Ownership = assignment.Equipment.Ownership,
            Condition = assignment.Equipment.Condition,
            EquipmentStatus = assignment.Equipment.Status,
            ProjectId = assignment.ProjectId,
            ResponsibleUserId = assignment.ResponsibleUserId,
            OperatorName = assignment.OperatorName,
            StartDate = assignment.StartDate,
            EndDate = assignment.EndDate,
            Notes = assignment.Notes,
            AssignedAt = assignment.AssignedAt,
            AssignedBy = assignment.AssignedBy,
            EndedAt = assignment.EndedAt,
            EndedBy = assignment.EndedBy,
            RowVersion = assignment.RowVersion,
        });
}

/// <summary>
/// One stay of a machine on a project, as every assignment endpoint and the machine's history return it.
/// The project's code and names are filled only for a project the caller may see.
/// </summary>
internal sealed record EquipmentAssignmentView(
    int Id,
    int EquipmentId,
    string AssetNo,
    string EquipmentNameAr,
    string EquipmentNameEn,
    string Category,
    string TypeName,
    string Ownership,
    string Condition,
    string EquipmentStatus,
    int ProjectId,
    string? ProjectCode,
    string? ProjectNameAr,
    string? ProjectNameEn,
    FleetPerson? Responsible,
    string? OperatorName,
    DateOnly StartDate,
    DateOnly? EndDate,
    bool IsCurrent,
    string? Notes,
    DateTime AssignedAt,
    FleetPerson AssignedBy,
    DateTime? EndedAt,
    FleetPerson? EndedBy,
    string RowVersion)
{
    public static EquipmentAssignmentView From(AssignmentRow row, ProjectAccessInfo? project, IReadOnlyDictionary<int, UserSummary> people) =>
        new(
            row.Id,
            row.EquipmentId,
            row.AssetNo,
            row.EquipmentNameAr,
            row.EquipmentNameEn,
            row.Category.ToString(),
            row.TypeName,
            row.Ownership.ToString(),
            row.Condition.ToString(),
            row.EquipmentStatus.ToString(),
            row.ProjectId,
            project?.Code,
            project?.NameAr,
            project?.NameEn,
            FleetPerson.FromOptional(row.ResponsibleUserId, people),
            row.OperatorName,
            row.StartDate,
            row.EndDate,
            row.EndDate is null,
            row.Notes,
            row.AssignedAt,
            FleetPerson.From(row.AssignedBy, people),
            row.EndedAt,
            FleetPerson.FromOptional(row.EndedBy, people),
            RowVersions.Format(row.RowVersion));

    /// <summary>The views of one project's rows, with one name lookup for every person on them.</summary>
    public static async Task<IReadOnlyList<EquipmentAssignmentView>> FromAsync(
        IReadOnlyList<AssignmentRow> rows, ProjectAccessInfo project, IUserDirectory users, CancellationToken cancellationToken)
    {
        var people = await Lookups.UsersAsync(
            users,
            rows.SelectMany(row => new[] { row.ResponsibleUserId, row.AssignedBy, row.EndedBy }),
            cancellationToken);

        return rows.Select(row => From(row, project, people)).ToList();
    }
}
