using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Data;

namespace AlMotaheda.Modules.Access.Common;

/// <summary>Parses a permission map sent by a client: module code to action names.</summary>
internal static class PermissionInput
{
    public static HashSet<(string Module, PermissionAction Action)>? Parse(
        Dictionary<string, string[]?>? permissions, ValidationErrors errors, string field = "permissions")
    {
        if (permissions is null)
        {
            errors.Add(field, "permissions is required. Send every module that should have access; leave others out.");
            return null;
        }

        var result = new HashSet<(string, PermissionAction)>();

        foreach (var (module, actions) in permissions)
        {
            if (!PermissionCatalog.IsKnownModule(module))
            {
                errors.Add($"{field}.{module}", "Unknown module.");
                continue;
            }

            foreach (var name in actions ?? [])
            {
                if (PermissionActions.TryParse(name, out var action))
                {
                    result.Add((module, action));
                }
                else
                {
                    errors.Add($"{field}.{module}", $"Unknown action '{name}'. Use View, Create, Edit, Approve or SeeMoney.");
                }
            }
        }

        return result;
    }

    /// <summary>For logs: the actions of one module as letters, for example "VCE", or "-" for none.</summary>
    public static string Letters(IEnumerable<PermissionAction> actions)
    {
        var letters = string.Concat(actions.Distinct().Order().Select(PermissionActions.ToLetter));
        return letters.Length == 0 ? "-" : letters;
    }
}
