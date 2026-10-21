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
/// <c>PUT /api/users/{id}/permissions/{module}</c>: an Executive sets one module's actions for one user.
/// The module then shows as "Customised": the user's setting replaces the role's for all five actions.
/// </summary>
internal static class SetUserModulePermissions
{
    internal sealed record Request(string[]? Actions, string? RowVersion);

    public static void Map(IEndpointRouteBuilder users) =>
        users.MapPut("/{id:int}/permissions/{module}", HandleAsync)
            .RequireExecutive()
            .WithName("SetUserModulePermissions")
            .WithSummary("Customise one module's permissions for a user (Executive only)");

    internal static async Task<Results<Ok<UserPermissionsView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int id,
        string module,
        Request request,
        HttpContext httpContext,
        AccessDbContext db,
        UserManagementGate gate,
        PermissionResolver resolver,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        if (!PermissionCatalog.IsKnownModule(module))
        {
            return ApiProblem.NotFoundResult("The module was not found.");
        }

        var errors = new ValidationErrors();
        var granted = new HashSet<PermissionAction>();
        if (request.Actions is null)
        {
            errors.Add("actions", "actions is required. Send an empty list to remove all access to this module.");
        }

        foreach (var name in request.Actions ?? [])
        {
            if (PermissionActions.TryParse(name, out var action))
            {
                granted.Add(action);
            }
            else
            {
                errors.Add("actions", $"Unknown action '{name}'. Use View, Create, Edit, Approve or SeeMoney.");
            }
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors);
        }

        var entry = await gate.EnterAsync(httpContext.User, "SetUserModulePermissions", cancellationToken);
        if (entry.Refusal is not null)
        {
            return entry.Refusal;
        }

        await using var scope = entry.Scope!;

        if (id == scope.ActorId)
        {
            log.ActionRefused(scope.ActorId, "SetUserModulePermissions", "changing own permissions");
            return AccessProblems.SelfChange();
        }

        var user = await UserRowLock.LoadForUpdateAsync(db, id, cancellationToken);
        if (user is null)
        {
            return ApiProblem.NotFoundResult("The user was not found.");
        }

        if (user.Role.Code == RoleCodes.Executive)
        {
            log.ActionRefused(scope.ActorId, "SetUserModulePermissions", "Executives can't have their own permissions");
            return AccessProblems.ExecutiveLocked();
        }

        var before = (await resolver.ResolveAsync(user.Id, user.RoleId, user.Role.Code, cancellationToken)).For(module).ToList();
        var now = time.GetUtcNow().UtcDateTime;
        var modulePermissionIds = PermissionActions.All.Select(action => PermissionCatalog.PermissionId(module, action)).ToList();

        // Removed through the change tracker, not a bulk delete, so the audit trail keeps every removed grant.
        db.UserPermissionOverrides.RemoveRange(await db.UserPermissionOverrides
            .Where(link => link.UserId == user.Id && modulePermissionIds.Contains(link.PermissionId))
            .ToListAsync(cancellationToken));

        // One row per action, granted or denied, so the role's setting no longer applies to this module.
        db.UserPermissionOverrides.AddRange(PermissionActions.All.Select(action => new UserPermissionOverride
        {
            UserId = user.Id,
            PermissionId = PermissionCatalog.PermissionId(module, action),
            IsGranted = granted.Contains(action),
            CreatedAt = now,
            CreatedBy = scope.ActorId,
        }));

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
        log.UserPermissionsChanged(scope.ActorId, user.Id, module, PermissionInput.Letters(before), PermissionInput.Letters(granted));

        var view = await UserPermissionsView.LoadAsync(db, resolver, user.Id, cancellationToken);
        return TypedResults.Ok(view!);
    }
}
