namespace AlMotaheda.Modules.Fleet.Data;

/// <summary>
/// One stay of a machine on a project, from a start date to an end date. The open row (no end date) says where the
/// machine is today; a filtered unique index allows one open row per machine, so a machine is on one project at a
/// time. Ending a stay sets its end date: the row is kept as history and never deleted.
/// </summary>
internal sealed class EquipmentAssignment
{
    public int Id { get; set; }

    public int EquipmentId { get; set; }
    public Equipment Equipment { get; set; } = null!;

    /// <summary>The project (<c>projects.Project</c>). A plain id: the Projects module owns the table.</summary>
    public int ProjectId { get; set; }

    /// <summary>The engineer or user who answers for the machine on site (<c>access.User</c>), if anyone.</summary>
    public int? ResponsibleUserId { get; set; }

    /// <summary>The operator, as typed: operators are often workers without a login.</summary>
    public string? OperatorName { get; set; }

    public DateOnly StartDate { get; set; }

    /// <summary>Empty while the machine is on the project.</summary>
    public DateOnly? EndDate { get; set; }

    public string? Notes { get; set; }

    public DateTime AssignedAt { get; set; }
    public int AssignedBy { get; set; }

    /// <summary>Set together with <see cref="EndDate"/> and <see cref="EndedBy"/>.</summary>
    public DateTime? EndedAt { get; set; }
    public int? EndedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
