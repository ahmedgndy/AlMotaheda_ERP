using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>
/// Changes that check a sum before saving (approve, allocate, pay) run one at a time per resource: each takes an
/// application lock inside its transaction, so two requests can't both pass the check and both save.
/// </summary>
/// <remarks>
/// <c>sp_getapplock</c> owned by the transaction, released when it commits or rolls back, with a 10-second wait.
/// Callers answer 503 <c>busy</c> when the lock isn't granted. Each module names its own resources
/// (for example <c>AlMotaheda.Workforce.Worker.12</c>), so modules never wait on each other's locks.
/// </remarks>
public static class SqlAppLock
{
    private const int TimeoutMilliseconds = 10_000;

    /// <summary>Takes the lock called <paramref name="resource"/>. Must run inside a transaction. False on timeout.</summary>
    public static async Task<bool> TryAcquireAsync(DbContext db, string resource, CancellationToken cancellationToken)
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
