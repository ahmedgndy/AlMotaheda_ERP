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
/// <c>POST /api/auth/login</c>: checks email and password, then starts a sign-in (access token in the body,
/// refresh token in an HttpOnly cookie).
/// </summary>
/// <remarks>
/// Every failure (unknown email, disabled, locked, wrong password) returns the same 401 and costs a password-hash
/// check, so the endpoint can't easily be used to find out which emails exist.
/// </remarks>
internal static class Login
{
    internal sealed record Request(string? Email, string? Password, bool RememberMe);

    public static void Map(IEndpointRouteBuilder auth) =>
        auth.MapPost("/login", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication)
            .WithName("Login")
            .WithSummary("Sign in with email and password");

    internal static async Task<Results<Ok<SessionResponse>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        AccessDbContext db,
        PasswordService passwords,
        SignInAttempts attempts,
        SessionFactory sessions,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var email = InputRules.Clean(request.Email);
        if (email is null || email.Length > InputRules.EmailMaxLength)
        {
            errors.Add("email", "Enter your email address.");
        }

        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > InputRules.PasswordMaxLength)
        {
            errors.Add("password", "Enter your password.");
        }

        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors);
        }

        var password = request.Password!;
        var clientIp = httpContext.Connection.RemoteIpAddress;
        var normalizedEmail = InputRules.NormalizeEmail(email!);

        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, cancellationToken);

        if (user is null || user.Status != UserStatus.Active)
        {
            await attempts.SpendEquivalentTimeAsync(cancellationToken);
            passwords.VerifyAgainstDummy(password);
            log.SignInFailed(user is null ? "UnknownAccount" : "Disabled", user?.Id, clientIp);
            return AccessProblems.SignInFailed();
        }

        // Counted before the (slow) check, so parallel guesses can't get past the lockout.
        var reservation = await attempts.ReserveAsync(user.Id, cancellationToken);
        if (reservation is null)
        {
            passwords.VerifyAgainstDummy(password);
            log.SignInFailed("LockedOut", user.Id, clientIp);
            return AccessProblems.SignInFailed();
        }

        var check = passwords.Verify(user, password);
        if (check == PasswordCheck.Wrong)
        {
            attempts.RecordFailure(reservation);
            log.SignInFailed("WrongPassword", user.Id, clientIp);
            return AccessProblems.SignInFailed();
        }

        NewRefreshToken refreshToken;
        SessionResponse session;

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Re-read under a row lock: if the password was reset or changed, or the user disabled, while the
            // password was being checked, this sign-in must not go through.
            var current = await UserRowLock.ReadAsync(db, user.Id, cancellationToken);
            if (current is null || current.Status != UserStatus.Active || current.PasswordHash != user.PasswordHash)
            {
                log.SignInFailed("ChangedDuringSignIn", user.Id, clientIp);
                return AccessProblems.SignInFailed();
            }

            await attempts.RecordSuccessAsync(user.Id, signedIn: true, cancellationToken);

            if (check == PasswordCheck.CorrectButOutdatedHash)
            {
                var upgradedHash = passwords.Hash(user, password);
                await db.Users
                    .Where(candidate => candidate.Id == user.Id && candidate.PasswordHash == user.PasswordHash)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.PasswordHash, upgradedHash), cancellationToken);
            }

            refreshToken = sessions.AddNewSignIn(user.Id, request.RememberMe);
            session = await sessions.CreateResponseAsync(current.ToSessionUser(), refreshToken, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        RefreshCookie.Write(httpContext.Response, refreshToken);
        log.SignedIn(user.Id);

        return TypedResults.Ok(session);
    }
}
