using System.Security.Claims;

namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// The one question every module asks before it touches a project's records: may this caller see this project?
/// Implemented by the Projects module, which reads membership from the database on every call.
/// </summary>
public interface IProjectAccess
{
    /// <summary>
    /// The project as the caller may see it, or <c>null</c> when it doesn't exist or is outside the caller's scope.
    /// Both cases must be answered with the same 404, so a guessed id reveals nothing.
    /// </summary>
    Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken cancellationToken);
}

/// <summary>A project the caller may see, and what the caller is on it.</summary>
/// <param name="IsCompleted">A completed project is read-only for everyone except an Executive.</param>
/// <param name="CallerResponsibility">One of <see cref="ProjectResponsibilities"/>, or <c>null</c> when the caller isn't on the team.</param>
public sealed record ProjectAccessInfo(
    int ProjectId,
    string Code,
    string NameAr,
    string NameEn,
    bool IsCompleted,
    string? CallerResponsibility,
    bool CallerIsExecutive)
{
    /// <summary>True when the caller is this project's active manager.</summary>
    public bool CallerIsManager => CallerResponsibility == ProjectResponsibilities.ProjectManager;

    /// <summary>True when new records and changes must be refused with 409 <c>project_completed</c>.</summary>
    public bool WritesBlocked => IsCompleted && !CallerIsExecutive;
}

/// <summary>What a member does on a project, as text. The same names as the Projects module returns.</summary>
public static class ProjectResponsibilities
{
    public const string ProjectManager = "ProjectManager";
    public const string SiteEngineer = "SiteEngineer";
    public const string TeamMember = "TeamMember";
}
