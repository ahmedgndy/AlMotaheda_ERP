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
/// <c>POST /api/auth/logout</c>: ends this sign-in (every token in its family) and clears the cookie.
/// Always returns 204, even when already signed out, so the frontend can call it unconditionally.
/// </summary>
/// <remarks>
/// The access token the browser holds keeps working until it expires (at most 15 minutes);
/// the frontend drops it from memory.
/// </remarks>
internal static class Logout
{
    public static void Map(IEndpointRouteBuilder auth) =>
        auth.MapPost("/logout", HandleAsync)
            .AllowAnonymous()
            .WithName("Logout")
            .WithSummary("Sign out of this browser");

    internal static async Task<NoContent> HandleAsync(
        HttpContext httpContext,
        AccessDbContext db,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        var tokenHash = RefreshTokenValues.TryHash(RefreshCookie.Read(httpContext.Request));

        if (tokenHash is not null)
        {
            var token = await db.RefreshTokens
                .AsNoTracking()
                .Where(candidate => candidate.TokenHash == tokenHash)
                .Select(candidate => new { candidate.UserId, candidate.FamilyId })
                .SingleOrDefaultAsync(cancellationToken);

            if (token is not null)
            {
                var revoked = await SessionRevocation.RevokeFamilyAsync(
                    db, token.FamilyId, RefreshTokenRevocation.SignedOut, time.GetUtcNow().UtcDateTime, cancellationToken);

                if (revoked > 0)
                {
                    log.SignedOut(token.UserId);
                }
            }
        }

        RefreshCookie.Delete(httpContext.Response);
        return TypedResults.NoContent();
    }
}
