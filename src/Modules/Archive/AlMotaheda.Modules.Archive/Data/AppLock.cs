using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Data;

/// <summary>
/// Changes that check a sum before saving (approve, allocate, pay) run one at a time per resource: each takes an
/// application lock inside its transaction, so two requests can't both pass the check and both save.
/// </summary>
/// <remarks>
/// The same pattern as the Projects module's project lock: <c>sp_getapplock</c> owned by the transaction, released
/// when it commits or rolls back, with a 10-second wait. Waiting longer answers 503 <c>busy</c>.
/// </remarks>
internal static class AppLock
{
    private const int TimeoutMilliseconds = 10_000;

    /// <summary>The lock name for the records of one project in this module.</summary>
    public static string Project(int projectId) => $"AlMotaheda.Archive.Project.{projectId}";

    /// <summary>Takes the lock called <paramref name="resource"/>. Must run inside a transaction. False on timeout.</summary>
    public static async Task<bool> TryAcquireAsync(ArchiveDbContext db, string resource, CancellationToken cancellationToken)
    {
        // sp_getapplock returns 0 or 1 when granted, and a negative number on timeout or deadlock.
        var result = await db.Database
            .SqlQuery<int>($"""
                DECLARE @result int;
                EXEC @result = sp_getapplock
                    @Resource = {resource},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = {TimeoutMilliseconds};
                SELECT @result AS [Value];
                """)
            .ToListAsync(cancellationToken);

        return result.Count == 1 && result[0] >= 0;
    }
}
