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

namespace AlMotaheda.Modules.Access.Features.UserPermissions;

/// <summary>
/// <c>POST /api/users/{id}/permissions/reset</c>: an Executive removes all of a user's own settings, so the role's
/// permissions apply again (the prototype's "Reset to role defaults").
/// </summary>
internal static class ResetUserPermissions
{
    internal sealed record Request(string? RowVersion);

    public static void Map(IEndpointRouteBuilder users) =>
        users.MapPost("/{id:int}/permissions/reset", HandleAsync)
            .RequireExecutive()
            .WithName("ResetUserPermissions")
            .WithSummary("Go back to the role's permissions (Executive only)");

    internal static async Task<Results<Ok<UserPermissionsView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int id,
        Request request,
        HttpContext httpContext,
        AccessDbContext db,
        UserManagementGate gate,
        PermissionResolver resolver,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors);
        }

        var entry = await gate.EnterAsync(httpContext.User, "ResetUserPermissions", cancellationToken);
        if (entry.Refusal is not null)
        {
            return entry.Refusal;
        }

        await using var scope = entry.Scope!;

        if (id == scope.ActorId)
        {
            log.ActionRefused(scope.ActorId, "ResetUserPermissions", "changing own permissions");
            return AccessProblems.SelfChange();
        }

        var user = await UserRowLock.LoadForUpdateAsync(db, id, cancellationToken);
        if (user is null)
        {
            return ApiProblem.NotFoundResult("The user was not found.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        // Removed through the change tracker, not a bulk delete, so the audit trail keeps every removed override.
        db.UserPermissionOverrides.RemoveRange(await db.UserPermissionOverrides.Where(link => link.UserId == user.Id).ToListAsync(cancellationToken));

        user.UpdatedAt = now;
        user.UpdatedBy = scope.ActorId;
        db.Entry(user).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await scope.CommitAsync(cancellationToken);
        log.UserPermissionsReset(scope.ActorId, user.Id);

        var view = await UserPermissionsView.LoadAsync(db, resolver, user.Id, cancellationToken);
        return TypedResults.Ok(view!);
    }
}
