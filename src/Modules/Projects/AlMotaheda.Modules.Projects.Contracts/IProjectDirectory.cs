namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// Project names and people for screens that span projects (a company's account across every project it works on)
/// and for modules that tell a project's manager about something. Implemented by Projects.
/// </summary>
/// <remarks>Never a permission check: callers first narrow the ids with <see cref="IVisibleProjects"/>.</remarks>
public interface IProjectDirectory
{
    /// <summary>The projects with these ids, in one query. Unknown ids are left out.</summary>
    Task<IReadOnlyDictionary<int, ProjectName>> FindAsync(IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken);

    /// <summary>Project items with these ids, on any project, in one query (for screens that span projects).</summary>
    Task<IReadOnlyDictionary<int, ProjectItemRef>> ItemsAsync(IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken);

    /// <summary>The project's active managers (normally one).</summary>
    Task<IReadOnlyList<int>> ManagerIdsAsync(int projectId, CancellationToken cancellationToken);
}

public sealed record ProjectName(int Id, string Code, string NameAr, string NameEn);
