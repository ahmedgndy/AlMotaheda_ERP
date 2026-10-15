using System.Security.Claims;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Workforce.Security;

/// <summary>
/// Which of a worker's projects the caller may see. Workers are company-wide, but their assignments, rates, pay
/// and payments belong to projects, and a project the caller can't see must not leak through the worker screens.
/// </summary>
internal static class ProjectVisibility
{
    /// <summary>
    /// The ids among <paramref name="projectIds"/> that the caller may see. The Projects contract answers one project
    /// at a time, so this asks once per distinct id. A worker works on few projects, so this is a handful of calls.
    /// </summary>
    public static async Task<IReadOnlySet<int>> AmongAsync(
        IProjectAccess access, ClaimsPrincipal caller, IEnumerable<int> projectIds, CancellationToken cancellationToken)
    {
        var visible = new HashSet<int>();
        foreach (var projectId in projectIds.Distinct())
        {
            if (await access.FindVisibleAsync(caller, projectId, cancellationToken) is not null)
            {
                visible.Add(projectId);
            }
        }

        return visible;
    }
}
