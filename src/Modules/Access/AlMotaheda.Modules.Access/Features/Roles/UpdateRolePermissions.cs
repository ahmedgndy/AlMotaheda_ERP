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

namespace AlMotaheda.Modules.Access.Features.Roles;

/// <summary>
/// <c>PUT /api/roles/{code}/permissions</c>: an Executive replaces one role's permissions for the whole company.
/// The Executive role itself can't be edited (Q9). Signed-in users get the change at their next refresh (Q8).
/// </summary>
internal static class UpdateRolePermissions
{
    internal sealed record Request(Dictionary<string, string[]?>? Permissions, string? RowVersion);

    public static void Map(IEndpointRouteBuilder roles) =>
        roles.MapPut("/{code}/permissions", HandleAsync)
            .RequireExecutive()
            .WithName("UpdateRolePermissions")
            .WithSummary("Replace a role's permissions (Executive only)");

    internal static async Task<Results<Ok<RoleView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        string code,
        Request request,
        HttpContext httpContext,
        AccessDbContext db,
        UserManagementGate gate,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        if (!PermissionCatalog.IsKnownRole(code))
        {
            return ApiProblem.NotFoundResult("The role was not found.");
        }

        if (code == RoleCodes.Executive)
        {
            log.ActionRefused(httpContext.User.GetUserId(), "UpdateRolePermissions", "the Executive role is fixed");
            return AccessProblems.ExecutiveLocked();
        }

        var errors = new ValidationErrors();
        var requested = PermissionInput.Parse(request.Permissions, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors);
        }

        var entry = await gate.EnterAsync(httpContext.User, "UpdateRolePermissions", cancellationToken);
        if (entry.Refusal is not null)
        {
            return entry.Refusal;
        }

        await using var scope = entry.Scope!;

        var role = await db.Roles.SingleAsync(candidate => candidate.Code == code, cancellationToken);

        var current = await db.RolePermissions
            .Where(link => link.RoleId == role.Id)
            .Select(link => new { link.PermissionId, link.Permission.Module, link.Permission.Action })
            .ToListAsync(cancellationToken);

        var wanted = requested!.ToDictionary(entry => PermissionCatalog.PermissionId(entry.Module, entry.Action), entry => entry);
        var removedIds = current.Where(link => !wanted.ContainsKey(link.PermissionId)).Select(link => link.PermissionId).ToList();
        var addedIds = wanted.Keys.Except(current.Select(link => link.PermissionId)).ToList();
        var now = time.GetUtcNow().UtcDateTime;

        if (removedIds.Count > 0)
        {
            // Removed through the change tracker, not a bulk delete, so the audit trail keeps every removed permission.
            db.RolePermissions.RemoveRange(await db.RolePermissions
                .Where(link => link.RoleId == role.Id && removedIds.Contains(link.PermissionId))
                .ToListAsync(cancellationToken));
        }

        db.RolePermissions.AddRange(addedIds.Select(permissionId => new RolePermission
        {
            RoleId = role.Id,
            PermissionId = permissionId,
            CreatedAt = now,
            CreatedBy = scope.ActorId,
        }));

        // Touching the role row makes its RowVersion the guard for the whole matrix row:
        // a second editor working from the old version gets a conflict instead of overwriting.
        role.UpdatedAt = now;
        role.UpdatedBy = scope.ActorId;
        db.Entry(role).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await scope.CommitAsync(cancellationToken);

        foreach (var module in PermissionCatalog.Modules)
        {
            var before = current.Where(link => link.Module == module).Select(link => link.Action).ToList();
            var after = wanted.Values.Where(entry => entry.Module == module).Select(entry => entry.Action).ToList();
            if (!before.Order().SequenceEqual(after.Order()))
            {
                log.RolePermissionsChanged(scope.ActorId, code, module, PermissionInput.Letters(before), PermissionInput.Letters(after));
            }
        }

        var view = await RoleView.LoadAsync(db, code, cancellationToken);
        return TypedResults.Ok(view.Single());
    }
}
