using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Security;

/// <summary>
/// Changes to one project's team, and edits that depend on who manages it, run one at a time: each takes this
/// project's application lock inside its transaction. Two Executives replacing the manager at once can't both
/// succeed, and a manager's edit can't land after an Executive has replaced them.
/// </summary>
/// <remarks>
/// The same pattern as the Access module's user-management lock: <c>sp_getapplock</c> owned by the transaction,
/// released when it commits or rolls back, with a 10-second wait. Waiting longer answers 503 <c>busy</c>.
/// </remarks>
internal static class ProjectLock
{
    private const int TimeoutMilliseconds = 10_000;

    /// <summary>Takes the lock for <paramref name="projectId"/>. Must run inside a transaction. False on timeout.</summary>
    public static async Task<bool> TryAcquireAsync(ProjectsDbContext db, int projectId, CancellationToken cancellationToken)
    {
        var resource = $"AlMotaheda.Projects.Project.{projectId}";

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
