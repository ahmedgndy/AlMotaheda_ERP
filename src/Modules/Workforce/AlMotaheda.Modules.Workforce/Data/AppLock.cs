namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>
/// The application-lock names of this module (see <see cref="SqlAppLock"/>). Changes that check a sum before saving
/// take one of these locks inside their transaction, so two requests can't both pass the check and both save.
/// </summary>
internal static class AppLock
{
    /// <summary>The lock name for the records of one project in this module.</summary>
    public static string Project(int projectId) => $"AlMotaheda.Workforce.Project.{projectId}";

    /// <summary>The lock name for one worker's payments: a worker is paid across projects, so the lock is per worker.</summary>
    public static string Worker(int workerId) => $"AlMotaheda.Workforce.Worker.{workerId}";

    /// <summary>Takes the lock called <paramref name="resource"/>. Must run inside a transaction. False on timeout.</summary>
    public static Task<bool> TryAcquireAsync(WorkforceDbContext db, string resource, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, resource, cancellationToken);
}
