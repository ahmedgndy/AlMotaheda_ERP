using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using AlMotaheda.Modules.Access.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Features.Auth;

/// <summary>
/// <c>POST /api/auth/refresh</c>: swaps the refresh-token cookie for a new one and returns a new access token,
/// with the user's current role and permissions.
/// </summary>
/// <remarks>
/// A token that was already swapped coming back means two parties hold a copy. The API can't tell which one
/// is the real user, so it ends that whole sign-in (the token family) and both must sign in again.
/// </remarks>
internal static class Refresh
{
    public static void Map(IEndpointRouteBuilder auth) =>
        auth.MapPost("/refresh", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.TokenRefresh)
            .WithName("RefreshSession")
            .WithSummary("Get a new access token using the refresh-token cookie");

    internal static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        AccessDbContext db,
        SessionFactory sessions,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        var tokenHash = RefreshTokenValues.TryHash(RefreshCookie.Read(httpContext.Request));
        if (tokenHash is null)
        {
            return Ended(httpContext);
        }

        var now = time.GetUtcNow().UtcDateTime;

        var stored = await db.RefreshTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);

        if (stored is null)
        {
            return Ended(httpContext);
        }

        if (stored.RevokedReason == RefreshTokenRevocation.Rotated)
        {
            return await ReplayDetectedAsync(httpContext, db, stored, now, log, cancellationToken);
        }

        if (stored.RevokedAt is not null || stored.ExpiresAt <= now)
        {
            return Ended(httpContext);
        }

        NewRefreshToken replacement;
        SessionResponse session;

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Under a row lock, so a disable, password reset or password change either finishes first
            // (and this refresh sees it) or waits and then revokes the token issued here.
            var user = await UserRowLock.ReadAsync(db, stored.UserId, cancellationToken);
            if (user is null || user.Status != UserStatus.Active)
            {
                return Ended(httpContext);
            }

            // Only one request can retire this token: the UPDATE matches only while it is still active.
            var retired = await db.RefreshTokens
                .Where(token => token.Id == stored.Id && token.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(token => token.RevokedAt, now)
                        .SetProperty(token => token.RevokedReason, RefreshTokenRevocation.Rotated),
                    cancellationToken);

            if (retired == 0)
            {
                await transaction.RollbackAsync(cancellationToken);

                // Revoked in the meantime. Only a parallel refresh (same token, already rotated) is a replay;
                // a sign-out, password change or disable that got there first is not.
                var reason = await db.RefreshTokens
                    .Where(token => token.Id == stored.Id)
                    .Select(token => token.RevokedReason)
                    .SingleAsync(cancellationToken);

                return reason == RefreshTokenRevocation.Rotated
                    ? await ReplayDetectedAsync(httpContext, db, stored, now, log, cancellationToken)
                    : Ended(httpContext);
            }

            replacement = sessions.AddRotation(stored);
            session = await sessions.CreateResponseAsync(user.ToSessionUser(), replacement, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        RefreshCookie.Write(httpContext.Response, replacement);
        return TypedResults.Ok(session);
    }

    private static async Task<ProblemHttpResult> ReplayDetectedAsync(
        HttpContext httpContext, AccessDbContext db, RefreshToken stored, DateTime now, SecurityLog log, CancellationToken cancellationToken)
    {
        await SessionRevocation.RevokeFamilyAsync(db, stored.FamilyId, RefreshTokenRevocation.ReuseDetected, now, cancellationToken);
        log.RefreshTokenReplayed(stored.UserId, stored.FamilyId, httpContext.Connection.RemoteIpAddress);
        return Ended(httpContext);
    }

    private static ProblemHttpResult Ended(HttpContext httpContext)
    {
        RefreshCookie.Delete(httpContext.Response);
        return AccessProblems.SessionEnded();
    }
}
