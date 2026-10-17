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

/// <summary>
/// Finds projects by id for modules that don't scope by caller (HR records employees' days on projects). It never
/// grants access: callers that show project data still check <see cref="IProjectAccess"/>.
/// </summary>
public interface IProjectDirectory
{
    /// <summary>The projects with these ids, in one query. Unknown ids are left out.</summary>
    Task<IReadOnlyDictionary<int, ProjectRef>> FindAsync(IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken);

    /// <summary>The projects that are not completed, by code: a picker for modules that record against projects.</summary>
    Task<IReadOnlyList<ProjectRef>> ListOpenAsync(CancellationToken cancellationToken);

    /// <summary>The active items of one project, by English name: a picker, with no money.</summary>
    Task<IReadOnlyList<ProjectItemRef>> ListActiveItemsAsync(int projectId, CancellationToken cancellationToken);
}

public sealed record ProjectRef(int Id, string Code, string NameAr, string NameEn, bool IsCompleted);

/// <summary>
/// "Which of these users may see this project right now?", from the database: company-wide roles see every project,
/// everyone else only the projects they are an active member of. Used to choose who hears about a project's records.
/// </summary>
public interface IProjectAudience
{
    Task<IReadOnlySet<int>> WhoSeeAsync(int projectId, IReadOnlyCollection<int> userIds, CancellationToken cancellationToken);
}
