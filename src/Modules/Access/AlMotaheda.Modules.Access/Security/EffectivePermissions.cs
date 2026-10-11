using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Security;

/// <summary>
/// What one user may do, per module, after applying their overrides to their role's permissions.
/// </summary>
/// <remarks>
/// Resolution order (design, "Permissions"): if the user has an override for a module, the override decides
/// all five actions for that module; otherwise the role's current permissions decide. Executives always use
/// the Executive role's permissions.
/// </remarks>
internal sealed class EffectivePermissions
{
    private readonly Dictionary<string, SortedSet<PermissionAction>> _actionsByModule;

    private EffectivePermissions(Dictionary<string, SortedSet<PermissionAction>> actionsByModule, HashSet<string> overriddenModules)
    {
        _actionsByModule = actionsByModule;
        OverriddenModules = overriddenModules;
    }

    /// <summary>Modules where the user's override replaces the role's permissions ("Customised").</summary>
    public IReadOnlySet<string> OverriddenModules { get; }

    public static EffectivePermissions Combine(
        IEnumerable<(string Module, PermissionAction Action)> rolePermissions,
        IEnumerable<(string Module, PermissionAction Action, bool IsGranted)> userOverrides,
        bool ignoreOverrides)
    {
        var overrides = ignoreOverrides
            ? new List<(string Module, PermissionAction Action, bool IsGranted)>()
            : userOverrides.ToList();
        var overriddenModules = overrides.Select(entry => entry.Module).ToHashSet(StringComparer.Ordinal);

        var actionsByModule = new Dictionary<string, SortedSet<PermissionAction>>(StringComparer.Ordinal);

        foreach (var (module, action) in rolePermissions.Where(entry => !overriddenModules.Contains(entry.Module)))
        {
            Add(actionsByModule, module, action);
        }

        foreach (var (module, action, _) in overrides.Where(entry => entry.IsGranted))
        {
            Add(actionsByModule, module, action);
        }

        return new EffectivePermissions(actionsByModule, overriddenModules);
    }

    public bool Has(string module, PermissionAction action) =>
        _actionsByModule.TryGetValue(module, out var actions) && actions.Contains(action);

    public IReadOnlyCollection<PermissionAction> For(string module) =>
        _actionsByModule.TryGetValue(module, out var actions) ? actions : [];

    /// <summary>One access-token claim per module with at least one action, for example <c>projects:VCEF</c>.</summary>
    public List<string> ToClaimValues() =>
        OrderedModules().Select(module => AccessClaims.FormatPermission(module, _actionsByModule[module])).ToList();

    /// <summary>The API shape: module code to action names, only for modules with at least one action.</summary>
    public Dictionary<string, string[]> ToResponse() =>
        OrderedModules().ToDictionary(
            module => module,
            module => _actionsByModule[module].Select(action => action.ToString()).ToArray(),
            StringComparer.Ordinal);

    private IEnumerable<string> OrderedModules() =>
        PermissionCatalog.Modules.Where(module => _actionsByModule.TryGetValue(module, out var actions) && actions.Count > 0);

    private static void Add(Dictionary<string, SortedSet<PermissionAction>> actionsByModule, string module, PermissionAction action)
    {
        if (!actionsByModule.TryGetValue(module, out var actions))
        {
            actionsByModule[module] = actions = [];
        }

        actions.Add(action);
    }
}

/// <summary>Loads a user's effective permissions from the database: two small indexed queries.</summary>
internal sealed class PermissionResolver(AccessDbContext db)
{
    public async Task<EffectivePermissions> ResolveAsync(int userId, int roleId, string roleCode, CancellationToken cancellationToken)
    {
        var rolePermissions = await db.RolePermissions
            .AsNoTracking()
            .Where(link => link.RoleId == roleId)
            .Select(link => new { link.Permission.Module, link.Permission.Action })
            .ToListAsync(cancellationToken);

        // Executives can't have overrides, so their query is skipped.
        var isExecutive = roleCode == RoleCodes.Executive;
        var overrides = new List<(string Module, PermissionAction Action, bool IsGranted)>();

        if (!isExecutive)
        {
            var rows = await db.UserPermissionOverrides
                .AsNoTracking()
                .Where(link => link.UserId == userId)
                .Select(link => new { link.Permission.Module, link.Permission.Action, link.IsGranted })
                .ToListAsync(cancellationToken);

            overrides.AddRange(rows.Select(row => (row.Module, row.Action, row.IsGranted)));
        }

        return EffectivePermissions.Combine(
            rolePermissions.Select(entry => (entry.Module, entry.Action)),
            overrides,
            ignoreOverrides: isExecutive);
    }
}
