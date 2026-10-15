using AlMotaheda.Modules.Projects.Data;

namespace AlMotaheda.Modules.Projects.Security;

/// <summary>
/// Changes to one project's team, and edits that depend on who manages it, run one at a time: each takes this
/// project's application lock inside its transaction. Two Executives replacing the manager at once can't both
/// succeed, and a manager's edit can't land after an Executive has replaced them.
/// </summary>
/// <remarks>
/// A <see cref="SqlAppLock"/>: owned by the transaction, released when it commits or rolls back, with a 10-second
/// wait. Waiting longer answers 503 <c>busy</c>.
/// </remarks>
internal static class ProjectLock
{
    /// <summary>Takes the lock for <paramref name="projectId"/>. Must run inside a transaction. False on timeout.</summary>
    public static Task<bool> TryAcquireAsync(ProjectsDbContext db, int projectId, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, $"AlMotaheda.Projects.Project.{projectId}", cancellationToken);
}
