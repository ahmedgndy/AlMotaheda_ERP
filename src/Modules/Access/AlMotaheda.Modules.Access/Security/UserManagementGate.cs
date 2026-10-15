using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AlMotaheda.Modules.Access.Security;

/// <summary>
/// Every change to users or permissions goes through here. It enforces the mandatory rule that only an active
/// Executive can manage users and permissions, checked in the database rather than trusted from the caller's
/// token, which can be up to 15 minutes old.
/// </summary>
/// <remarks>
/// <para>The caller must, at this moment: exist, be active, have the Executive role, have no pending password
/// change, and still be signed in with the sign-in this token belongs to ("sid"). The last check means that
/// signing out, a password change or reset, or a replayed refresh token immediately stops a copied access token
/// from managing users, instead of 15 minutes later.</para>
/// <para>Each change runs in a transaction that holds an application lock, so user-management changes run one
/// at a time. That closes a race the token check alone can't: two Executives disabling each other at the same
/// moment would otherwise leave the company with no active Executive.</para>
/// </remarks>
internal sealed class UserManagementGate(AccessDbContext db, TimeProvider time, SecurityLog log)
{
    private const string LockName = "AlMotaheda.Access.UserManagement";

    /// <summary>
    /// Starts the transaction and returns it, or returns the refusal to send back
    /// (403 when the caller isn't a currently signed-in active Executive, 503 when the lock isn't free).
    /// </summary>
    /// <param name="action">Names the operation in the security log when it's refused.</param>
    public async Task<GateResult> EnterAsync(ClaimsPrincipal caller, string action, CancellationToken cancellationToken)
    {
        var actorId = caller.GetUserId();
        var sessionId = caller.GetSessionId();
        if (actorId is null || sessionId is null)
        {
            log.ActionRefused(actorId, action, "token without user or sign-in");
            return GateResult.Refused(AccessProblems.ExecutiveOnly());
        }

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (!await TryAcquireLockAsync(cancellationToken))
            {
                await transaction.DisposeAsync();
                return GateResult.Refused(AccessProblems.TryAgainLater());
            }

            var now = time.GetUtcNow().UtcDateTime;

            var activeExecutiveIds = await db.Users
                .Where(user => user.Status == UserStatus.Active && user.Role.Code == RoleCodes.Executive)
                .Select(user => user.Id)
                .ToListAsync(cancellationToken);

            var actorCanAct = activeExecutiveIds.Contains(actorId.Value)
                && await db.Users.AnyAsync(user => user.Id == actorId && !user.MustChangePassword, cancellationToken)
                && await db.RefreshTokens.AnyAsync(
                    token => token.FamilyId == sessionId
                             && token.UserId == actorId
                             && token.RevokedAt == null
                             && token.ExpiresAt > now,
                    cancellationToken);

            if (!actorCanAct)
            {
                await transaction.DisposeAsync();
                log.ActionRefused(actorId, action, "not a currently signed-in active Executive");
                return GateResult.Refused(AccessProblems.ExecutiveOnly());
            }

            return GateResult.Entered(new UserManagementScope(transaction, activeExecutiveIds, actorId.Value));
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private Task<bool> TryAcquireLockAsync(CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, LockName, cancellationToken);
}

internal sealed record GateResult(UserManagementScope? Scope, ProblemHttpResult? Refusal)
{
    public static GateResult Entered(UserManagementScope scope) => new(scope, null);

    public static GateResult Refused(ProblemHttpResult refusal) => new(null, refusal);
}

/// <summary>An open user-management transaction, entered by an active Executive.</summary>
internal sealed class UserManagementScope(IDbContextTransaction transaction, IReadOnlyList<int> activeExecutiveIds, int actorId)
    : IAsyncDisposable
{
    public int ActorId { get; } = actorId;

    /// <summary>True when removing <paramref name="userId"/> from the active Executives would leave none.</summary>
    public bool IsLastActiveExecutive(int userId) =>
        activeExecutiveIds.Contains(userId) && activeExecutiveIds.All(id => id == userId);

    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

    /// <summary>Rolls back unless <see cref="CommitAsync"/> ran.</summary>
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
