using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Features.Roles;

/// <summary>One role with its current permissions, as the role-permissions matrix shows it.</summary>
internal sealed record RoleView(string Code, bool IsEditable, Dictionary<string, string[]> Permissions, string RowVersion)
{
    /// <summary>Loads roles and their permissions in two queries. Pass a code to load one role.</summary>
    public static async Task<List<RoleView>> LoadAsync(AccessDbContext db, string? code, CancellationToken cancellationToken)
    {
        var roles = await db.Roles
            .AsNoTracking()
            .Where(role => code == null || role.Code == code)
            .OrderBy(role => role.Id)
            .Select(role => new { role.Id, role.Code, role.RowVersion })
            .ToListAsync(cancellationToken);

        var roleIds = roles.Select(role => role.Id).ToList();

        var links = await db.RolePermissions
            .AsNoTracking()
            .Where(link => roleIds.Contains(link.RoleId))
            .Select(link => new { link.RoleId, link.Permission.Module, link.Permission.Action })
            .ToListAsync(cancellationToken);

        return roles
            .Select(role => new RoleView(
                role.Code,
                IsEditable: role.Code != RoleCodes.Executive,
                ToResponse(links.Where(link => link.RoleId == role.Id).Select(link => (link.Module, link.Action))),
                RowVersions.Format(role.RowVersion)))
            .ToList();
    }

    /// <summary>Module code to action names, in catalog order, only modules with at least one action.</summary>
    public static Dictionary<string, string[]> ToResponse(IEnumerable<(string Module, PermissionAction Action)> permissions)
    {
        var byModule = permissions.ToLookup(entry => entry.Module, entry => entry.Action);

        return PermissionCatalog.Modules
            .Where(module => byModule[module].Any())
            .ToDictionary(
                module => module,
                module => byModule[module].Distinct().Order().Select(action => action.ToString()).ToArray(),
                StringComparer.Ordinal);
    }
}
