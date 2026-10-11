namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// One person's time on one project, in one responsibility. This table alone decides who may open a project
/// (apart from the roles that see every project) and who is on its team.
/// </summary>
/// <remarks>
/// <para>Rows are never deleted. Ending a membership sets <see cref="EndDate"/>, so the history of who managed or
/// worked on a project is kept. A membership is active while <see cref="EndDate"/> is empty, and access follows
/// that: it starts when the row is added and stops when it is ended.</para>
/// <para><see cref="UserId"/> is an Access user account. Workers and guards are never members; they are assigned to
/// projects by the Workforce module.</para>
/// </remarks>
internal sealed class ProjectMember
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The user account (Access module). A foreign key to <c>access.User</c>; users are never deleted.</summary>
    public int UserId { get; set; }

    public MemberResponsibility Responsibility { get; set; }

    /// <summary>When the person started on the project. For information: access starts when the row is added.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Empty while active. Never before <see cref="StartDate"/>.</summary>
    public DateOnly? EndDate { get; set; }

    public DateTime AssignedAt { get; set; }
    public int AssignedBy { get; set; }

    public DateTime? EndedAt { get; set; }
    public int? EndedBy { get; set; }
    public string? EndReason { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>What a member does on the project. Stored by name.</summary>
internal enum MemberResponsibility
{
    /// <summary>Exactly one active per project. An Executive or a Manager.</summary>
    ProjectManager,

    /// <summary>Attendance, daily reports, monitoring. An Engineer.</summary>
    SiteEngineer,

    /// <summary>Other responsible people, for example drivers. A Manager, Engineer or Staff.</summary>
    TeamMember,
}
