using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using AlMotaheda.Modules.Access.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Features.UserPermissions;

/// <summary>
/// A user's effective permissions, the modules where their own setting replaces the role's ("Customised"),
/// and the user's rowVersion to send back with a change.
/// </summary>
internal sealed record UserPermissionsView(
    int UserId,
    string Role,
    Dictionary<string, string[]> Permissions,
    IReadOnlyList<string> OverriddenModules,
    string RowVersion)
{
    public static async Task<UserPermissionsView?> LoadAsync(
        AccessDbContext db, PermissionResolver resolver, int userId, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new { candidate.Id, candidate.RoleId, RoleCode = candidate.Role.Code, candidate.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return null;
        }

        var effective = await resolver.ResolveAsync(user.Id, user.RoleId, user.RoleCode, cancellationToken);

        return new UserPermissionsView(
            user.Id,
            user.RoleCode,
            effective.ToResponse(),
            PermissionCatalog.Modules.Where(effective.OverriddenModules.Contains).ToList(),
            RowVersions.Format(user.RowVersion));
    }
}
