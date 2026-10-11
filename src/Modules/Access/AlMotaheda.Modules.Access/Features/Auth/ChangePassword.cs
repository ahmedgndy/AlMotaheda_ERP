using AlMotaheda.BuildingBlocks.Authorization;
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
/// <c>POST /api/auth/change-password</c>: the signed-in user replaces their own password.
/// Works while a change is required (first sign-in, or after a reset). Ends every sign-in of the user and
/// returns a fresh one for this browser.
/// </summary>
/// <remarks>
/// Wrong current passwords count toward the same lockout as sign-in, so a copied access token can't be used to
/// guess the password here either.
/// </remarks>
internal static class ChangePassword
{
    internal sealed record Request(string? CurrentPassword, string? NewPassword);

    public static void Map(IEndpointRouteBuilder auth) =>
        auth.MapPost("/change-password", HandleAsync)
            .RequireAuthorization()
            .AllowDuringPasswordChange()
            .RequireRateLimiting(RateLimitPolicies.Authentication)
            .WithName("ChangePassword")
            .WithSummary("Change your own password");

    internal static async Task<Results<Ok<SessionResponse>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        AccessDbContext db,
        PasswordService passwords,
        SignInAttempts attempts,
        SessionFactory sessions,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        var userId = httpContext.User.GetUserId();
        var user = userId is null
            ? null
            : await db.Users.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
        {
            return AccessProblems.SessionEnded();
        }

        var errors = new ValidationErrors();
        if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > InputRules.PasswordMaxLength)
        {
            errors.Add("currentPassword", "Enter your current password.");
        }

        InputRules.NewPassword(request.NewPassword, user.Email, errors, "newPassword");
        if (!errors.HasErrors && request.NewPassword == request.CurrentPassword)
        {
            errors.Add("newPassword", "The new password must be different from the current one.");
        }

        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors, "newPassword");
        }

        var reservation = await attempts.ReserveAsync(user.Id, cancellationToken);
        if (reservation is null)
        {
            passwords.VerifyAgainstDummy(request.CurrentPassword!);
            return AccessProblems.WrongCurrentPassword();
        }

        if (passwords.Verify(user, request.CurrentPassword!) == PasswordCheck.Wrong)
        {
            attempts.RecordFailure(reservation);
            return AccessProblems.WrongCurrentPassword();
        }

        var persistent = await IsCurrentSignInPersistentAsync(httpContext, db, user.Id, cancellationToken);
        var newHash = passwords.Hash(user, request.NewPassword!);
        var now = time.GetUtcNow().UtcDateTime;

        NewRefreshToken refreshToken;
        SessionResponse session;

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Matches only if the password is still the one just checked, so two changes can't both win.
            // Updating the row first also locks it, so no sign-in can slip in before the revocation below.
            var changed = await db.Users
                .Where(candidate => candidate.Id == user.Id
                                    && candidate.PasswordHash == user.PasswordHash
                                    && candidate.Status == UserStatus.Active)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(candidate => candidate.PasswordHash, newHash)
                        .SetProperty(candidate => candidate.MustChangePassword, false)
                        .SetProperty(candidate => candidate.UpdatedAt, now)
                        .SetProperty(candidate => candidate.UpdatedBy, user.Id),
                    cancellationToken);

            if (changed == 0)
            {
                return ApiProblem.ConcurrencyConflictResult();
            }

            await attempts.RecordSuccessAsync(user.Id, signedIn: false, cancellationToken);
            await SessionRevocation.RevokeAllForUserAsync(db, user.Id, RefreshTokenRevocation.PasswordChanged, now, cancellationToken);

            var current = await UserRowLock.ReadAsync(db, user.Id, cancellationToken);
            refreshToken = sessions.AddNewSignIn(user.Id, persistent);
            session = await sessions.CreateResponseAsync(current!.ToSessionUser(), refreshToken, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        RefreshCookie.Write(httpContext.Response, refreshToken);
        log.PasswordChanged(user.Id);

        return TypedResults.Ok(session);
    }

    /// <summary>Keeps "Remember me" across the password change when this browser's sign-in had it.</summary>
    private static async Task<bool> IsCurrentSignInPersistentAsync(
        HttpContext httpContext, AccessDbContext db, int userId, CancellationToken cancellationToken)
    {
        var tokenHash = RefreshTokenValues.TryHash(RefreshCookie.Read(httpContext.Request));
        return tokenHash is not null
               && await db.RefreshTokens.AnyAsync(
                   token => token.TokenHash == tokenHash && token.UserId == userId && token.IsPersistent,
                   cancellationToken);
    }
}
