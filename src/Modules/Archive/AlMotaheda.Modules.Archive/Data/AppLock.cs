namespace AlMotaheda.Modules.Archive.Data;

/// <summary>
/// The application-lock names of this module (see <see cref="SqlAppLock"/>). Changes that check a sum before saving
/// take one of these locks inside their transaction, so two requests can't both pass the check and both save.
/// </summary>
internal static class AppLock
{
    /// <summary>The lock name for the records of one project in this module.</summary>
    public static string Project(int projectId) => $"AlMotaheda.Archive.Project.{projectId}";

    /// <summary>Takes the lock called <paramref name="resource"/>. Must run inside a transaction. False on timeout.</summary>
    public static Task<bool> TryAcquireAsync(ArchiveDbContext db, string resource, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, resource, cancellationToken);
}
