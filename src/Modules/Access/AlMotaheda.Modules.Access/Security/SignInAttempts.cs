using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Access.Security;

/// <summary>
/// Lockout that holds under parallel requests (Q6: 5 wrong passwords in a row lock the account for 15 minutes).
/// </summary>
/// <remarks>
/// <para>Every password check first reserves an attempt in one UPDATE statement, before the (slow) hash check.
/// The reservation counts the attempt as failed and fails when the account is locked, so no matter how many
/// guesses arrive at once, at most five are ever checked. The fifth reservation locks the account up front; a
/// correct password then clears the count and the lock.</para>
/// <para>A request that dies after reserving leaves its attempt counted as failed, and at worst a lock that ends
/// after 15 minutes. It can never leave the account stuck.</para>
/// </remarks>
internal sealed class SignInAttempts(
    AccessDbContext db,
    IOptions<AccessLockoutOptions> lockoutOptions,
    TimeProvider time,
    SecurityLog log)
{
    /// <summary>
    /// Reserves one password check. Returns <c>null</c> when the user is locked out: the password must then not
    /// be checked. Callers check the user is active first; the user row is re-checked under a lock before any
    /// token is issued.
    /// </summary>
    /// <remarks>
    /// Touches only the sign-in state row, never the user row, so it can't deadlock with a password reset or
    /// change (which lock the user row first).
    /// </remarks>
    public async Task<AttemptReservation?> ReserveAsync(int userId, CancellationToken cancellationToken)
    {
        var settings = lockoutOptions.Value;
        var now = time.GetUtcNow().UtcDateTime;
        DateTime lockoutEndsAt = now.Add(settings.Duration);
        var limit = settings.MaxFailedAttempts;

        // A lock that has expired starts a fresh count (1); otherwise the count goes up by one.
        // Reaching the limit locks the account immediately, for this attempt and any running in parallel.
        var counts = await db.Database
            .SqlQuery<int>($"""
                UPDATE [state]
                SET [state].[FailedLoginCount] =
                        CASE WHEN [state].[LockoutEndsAt] IS NOT NULL THEN 1 ELSE [state].[FailedLoginCount] + 1 END,
                    [state].[LockoutEndsAt] =
                        CASE WHEN (CASE WHEN [state].[LockoutEndsAt] IS NOT NULL THEN 1 ELSE [state].[FailedLoginCount] + 1 END) >= {limit}
                             THEN {lockoutEndsAt}
                             ELSE NULL
                        END
                OUTPUT CAST(inserted.[FailedLoginCount] AS int) AS [Value]
                FROM [access].[UserSignInState] AS [state]
                WHERE [state].[UserId] = {userId}
                  AND ([state].[LockoutEndsAt] IS NULL OR [state].[LockoutEndsAt] <= {now})
                """)
            .ToListAsync(cancellationToken);

        return counts.Count == 1
            ? new AttemptReservation(userId, counts[0] >= limit, lockoutEndsAt)
            : null;
    }

    /// <summary>
    /// Runs the same statement against no row, so a request for an unknown or disabled account takes about as
    /// long as one for a real account.
    /// </summary>
    public Task SpendEquivalentTimeAsync(CancellationToken cancellationToken) => ReserveAsync(0, cancellationToken);

    /// <summary>The checked password was wrong. The attempt is already counted; log the lockout if this one caused it.</summary>
    public void RecordFailure(AttemptReservation reservation)
    {
        if (reservation.LocksAccount)
        {
            log.LockedOut(reservation.UserId, reservation.LockoutEndsAt);
        }
    }

    /// <summary>Clears the count and any lock after a correct password. Runs in the caller's transaction.</summary>
    public Task RecordSuccessAsync(int userId, bool signedIn, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;

        return db.UserSignInStates
            .Where(state => state.UserId == userId)
            .ExecuteUpdateAsync(
                setters =>
                {
                    setters
                        .SetProperty(state => state.FailedLoginCount, (short)0)
                        .SetProperty(state => state.LockoutEndsAt, (DateTime?)null);

                    if (signedIn)
                    {
                        setters.SetProperty(state => state.LastLoginAt, now);
                    }
                },
                cancellationToken);
    }
}

/// <param name="LocksAccount">True when this attempt reached the limit: the account is locked unless the password is right.</param>
internal sealed record AttemptReservation(int UserId, bool LocksAccount, DateTime LockoutEndsAt);

/// <summary>
/// Reads a user and holds an update lock on their row until the transaction ends.
/// </summary>
/// <remarks>
/// Sign-in and refresh take this lock before issuing tokens; disable, password reset and password change update
/// the same row before revoking tokens. Whichever comes second waits, so a token can never be issued in the gap
/// between "check the user" and "revoke everything".
/// </remarks>
internal static class UserRowLock
{
    /// <summary>
    /// Loads the user for changing, locked until the transaction ends. User-management changes take this lock
    /// before touching the user's overrides or tokens, the same order sign-in and refresh use, so the two can't
    /// deadlock; and nothing else can change the row between the load and the save.
    /// </summary>
    public static Task<User?> LoadForUpdateAsync(AccessDbContext db, int userId, CancellationToken cancellationToken) =>
        db.Users
            .FromSql($"SELECT * FROM [access].[User] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {userId}")
            .Include(user => user.Role)
            .SingleOrDefaultAsync(cancellationToken);

    public static Task<LockedUser?> ReadAsync(AccessDbContext db, int userId, CancellationToken cancellationToken) =>
        db.Users
            .FromSql($"SELECT * FROM [access].[User] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {userId}")
            .AsNoTracking()
            .Select(user => new LockedUser(
                user.Id,
                user.Status,
                user.PasswordHash,
                user.MustChangePassword,
                user.RoleId,
                user.Role.Code,
                user.NameAr,
                user.NameEn))
            .SingleOrDefaultAsync(cancellationToken);
}

internal sealed record LockedUser(
    int Id,
    UserStatus Status,
    string PasswordHash,
    bool MustChangePassword,
    int RoleId,
    string RoleCode,
    string NameAr,
    string NameEn)
{
    public SessionUser ToSessionUser() => new(Id, RoleId, RoleCode, MustChangePassword, NameAr, NameEn);
}
