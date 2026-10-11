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

namespace AlMotaheda.Modules.Access.Features.Users;

/// <summary>
/// <c>POST /api/users/{id}/reset-password</c>: an Executive sets a new password for a user who forgot theirs
/// (the "forgot password" e-mail comes later, Q10). Clears the lockout, ends all of the user's sign-ins, and
/// makes them choose a new password at their next sign-in.
/// </summary>
/// <remarks>
/// Executive-only because resetting someone's password lets you sign in as them.
/// For your own password, use <c>/api/auth/change-password</c>.
/// </remarks>
internal static class ResetPassword
{
    internal sealed record Request(string? NewPassword, string? RowVersion);

    public static void Map(IEndpointRouteBuilder users) =>
        users.MapPost("/{id:int}/reset-password", HandleAsync)
            .RequireExecutive()
            .WithName("ResetUserPassword")
            .WithSummary("Set a new password for a user (Executive only)");

    internal static async Task<Results<Ok<UserDetails>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int id,
        Request request,
        HttpContext httpContext,
        AccessDbContext db,
        UserManagementGate gate,
        PasswordService passwords,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (string.IsNullOrEmpty(request.NewPassword))
        {
            errors.Add("newPassword", "Password is required.");
        }

        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors, "newPassword");
        }

        var entry = await gate.EnterAsync(httpContext.User, "ResetPassword", cancellationToken);
        if (entry.Refusal is not null)
        {
            return entry.Refusal;
        }

        await using var scope = entry.Scope!;

        if (id == scope.ActorId)
        {
            log.ActionRefused(scope.ActorId, "ResetPassword", "resetting own password");
            return AccessProblems.SelfChange();
        }

        var user = await UserRowLock.LoadForUpdateAsync(db, id, cancellationToken);
        if (user is null)
        {
            return ApiProblem.NotFoundResult("The user was not found.");
        }

        // The rules need the user's email, so they run after the lookup.
        InputRules.NewPassword(request.NewPassword, user.Email, errors, "newPassword");
        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors, "newPassword");
        }

        var now = time.GetUtcNow().UtcDateTime;

        user.PasswordHash = passwords.Hash(user, request.NewPassword!);
        user.MustChangePassword = true;
        user.UpdatedAt = now;
        user.UpdatedBy = scope.ActorId;
        db.Entry(user).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            // The user row first, so a sign-in running now can't issue a token after the revocation below.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        // A reset also lifts a lockout: the user gets a fresh start with the new password.
        await db.UserSignInStates
            .Where(state => state.UserId == user.Id)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(state => state.FailedLoginCount, (short)0)
                    .SetProperty(state => state.LockoutEndsAt, (DateTime?)null),
                cancellationToken);

        await SessionRevocation.RevokeAllForUserAsync(db, user.Id, RefreshTokenRevocation.PasswordReset, now, cancellationToken);

        await scope.CommitAsync(cancellationToken);
        log.PasswordReset(scope.ActorId, user.Id, user.Role.Code == RoleCodes.Executive);

        var details = await UserDetails.LoadAsync(db, user.Id, now, cancellationToken);
        return TypedResults.Ok(details!);
    }
}
