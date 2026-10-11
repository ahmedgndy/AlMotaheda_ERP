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
/// <c>POST /api/users/{id}/disable</c> and <c>/enable</c>: an Executive turns an account off or on.
/// Disabling ends all of the user's sign-ins at once; the access token they hold expires within 15 minutes (Q8).
/// </summary>
internal static class SetUserStatus
{
    /// <param name="RowVersion">
    /// Optional. When sent, the change is refused if the user changed since that read. Leave it out for an
    /// emergency disable: then nothing a user can do on their own (such as changing their password) can make
    /// the disable fail.
    /// </param>
    internal sealed record Request(string? RowVersion);

    public static void Map(IEndpointRouteBuilder users)
    {
        users.MapPost("/{id:int}/disable", (int id, Request request, HttpContext httpContext, AccessDbContext db,
                UserManagementGate gate, TimeProvider time, SecurityLog log, CancellationToken cancellationToken) =>
                HandleAsync(id, UserStatus.Disabled, request, httpContext, db, gate, time, log, cancellationToken))
            .RequireExecutive()
            .WithName("DisableUser")
            .WithSummary("Disable a user and end their sign-ins (Executive only)");

        users.MapPost("/{id:int}/enable", (int id, Request request, HttpContext httpContext, AccessDbContext db,
                UserManagementGate gate, TimeProvider time, SecurityLog log, CancellationToken cancellationToken) =>
                HandleAsync(id, UserStatus.Active, request, httpContext, db, gate, time, log, cancellationToken))
            .RequireExecutive()
            .WithName("EnableUser")
            .WithSummary("Enable a disabled user (Executive only)");
    }

    internal static async Task<Results<Ok<UserDetails>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int id,
        UserStatus newStatus,
        Request request,
        HttpContext httpContext,
        AccessDbContext db,
        UserManagementGate gate,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var rowVersion = request.RowVersion is null ? null : RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors);
        }

        var action = newStatus == UserStatus.Disabled ? "DisableUser" : "EnableUser";

        var entry = await gate.EnterAsync(httpContext.User, action, cancellationToken);
        if (entry.Refusal is not null)
        {
            return entry.Refusal;
        }

        await using var scope = entry.Scope!;

        if (id == scope.ActorId)
        {
            // The prototype's rule: "You cannot disable your own account."
            log.ActionRefused(scope.ActorId, action, "changing own status");
            return AccessProblems.SelfChange();
        }

        var user = await UserRowLock.LoadForUpdateAsync(db, id, cancellationToken);
        if (user is null)
        {
            return ApiProblem.NotFoundResult("The user was not found.");
        }

        var now = time.GetUtcNow().UtcDateTime;

        if (rowVersion is not null && !user.RowVersion.AsSpan().SequenceEqual(rowVersion))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (user.Status != newStatus)
        {
            if (newStatus == UserStatus.Disabled && user.Role.Code == RoleCodes.Executive && scope.IsLastActiveExecutive(user.Id))
            {
                log.ActionRefused(scope.ActorId, action, "would leave no active Executive");
                return AccessProblems.WouldRemoveLastExecutive();
            }

            user.Status = newStatus;
            user.UpdatedAt = now;
            user.UpdatedBy = scope.ActorId;

            try
            {
                // The user row first: it locks the row, so a sign-in or refresh running now either finished
                // before (and its token is revoked below) or waits and then sees the user disabled.
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ApiProblem.ConcurrencyConflictResult();
            }

            if (newStatus == UserStatus.Disabled)
            {
                await SessionRevocation.RevokeAllForUserAsync(db, user.Id, RefreshTokenRevocation.UserDisabled, now, cancellationToken);
            }

            await scope.CommitAsync(cancellationToken);
            log.UserStatusChanged(scope.ActorId, user.Id, newStatus.ToString());
        }

        var details = await UserDetails.LoadAsync(db, user.Id, now, cancellationToken);
        return TypedResults.Ok(details!);
    }
}
