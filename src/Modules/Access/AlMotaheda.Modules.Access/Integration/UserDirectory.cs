using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Integration;

/// <summary>The Access module's answer to <see cref="IUserDirectory"/>, for other modules.</summary>
internal sealed class UserDirectory(AccessDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<int, UserSummary>> FindAsync(
        IReadOnlyCollection<int> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<int, UserSummary>();
        }

        var ids = userIds.Distinct().ToArray();

        var users = await db.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new UserSummary(
                user.Id,
                user.NameAr,
                user.NameEn,
                user.JobTitleAr,
                user.JobTitleEn,
                user.Email,
                user.Phone,
                user.Role.Code,
                user.Status == UserStatus.Active))
            .ToListAsync(cancellationToken);

        return users.ToDictionary(user => user.Id);
    }

    public async Task<IReadOnlyList<UserSummary>> ListActiveAsync(
        IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        if (roleCodes.Count == 0)
        {
            return [];
        }

        var roles = roleCodes.Distinct(StringComparer.Ordinal).ToArray();

        return await db.Users
            .AsNoTracking()
            .Where(user => user.Status == UserStatus.Active && roles.Contains(user.Role.Code))
            .OrderBy(user => user.NameEn)
            .ThenBy(user => user.Id)
            .Select(user => new UserSummary(
                user.Id,
                user.NameAr,
                user.NameEn,
                user.JobTitleAr,
                user.JobTitleEn,
                user.Email,
                user.Phone,
                user.Role.Code,
                true))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> ListActiveHoldingAsync(string module, string action, CancellationToken cancellationToken)
    {
        if (!PermissionActions.TryParse(action, out var wanted))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown permission action.");
        }

        var permissionId = await db.Permissions.AsNoTracking()
            .Where(permission => permission.Module == module && permission.Action == wanted)
            .Select(permission => (int?)permission.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (permissionId is null)
        {
            return [];
        }

        // The same resolution as a sign-in (EffectivePermissions): an override on the module decides all of it;
        // otherwise the role does. Executives have no overrides.
        var active = db.Users.AsNoTracking().Where(user => user.Status == UserStatus.Active);

        var throughRole = await active
            .Where(user => db.RolePermissions.Any(link => link.RoleId == user.RoleId && link.PermissionId == permissionId)
                           && (user.Role.Code == RoleCodes.Executive
                               || !db.UserPermissionOverrides.Any(link => link.UserId == user.Id && link.Permission.Module == module)))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        var throughOverride = await active
            .Where(user => user.Role.Code != RoleCodes.Executive
                           && db.UserPermissionOverrides.Any(link => link.UserId == user.Id && link.PermissionId == permissionId && link.IsGranted))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        return throughRole.Concat(throughOverride).Distinct().Order().ToList();
    }
}
