namespace AlMotaheda.BuildingBlocks.Authorization;

/// <summary>
/// The five things a permission can allow on a module. Every endpoint asks for exactly one module and one action.
/// </summary>
/// <remarks>
/// The names are stored in the database and returned by the API, so renaming one is a breaking change.
/// </remarks>
public enum PermissionAction
{
    View,
    Create,
    Edit,

    /// <summary>Approve or review records (the prototype's "Approve / review").</summary>
    Approve,

    /// <summary>See money amounts (the prototype's "See money").</summary>
    SeeMoney,
}

/// <summary>
/// Converts actions to and from the one-letter codes carried inside access tokens (V, C, E, A, F).
/// </summary>
public static class PermissionActions
{
    /// <summary>Every action, in the order the prototype shows them.</summary>
    public static IReadOnlyList<PermissionAction> All { get; } =
    [
        PermissionAction.View,
        PermissionAction.Create,
        PermissionAction.Edit,
        PermissionAction.Approve,
        PermissionAction.SeeMoney,
    ];

    public static char ToLetter(PermissionAction action) => action switch
    {
        PermissionAction.View => 'V',
        PermissionAction.Create => 'C',
        PermissionAction.Edit => 'E',
        PermissionAction.Approve => 'A',
        PermissionAction.SeeMoney => 'F',
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown permission action."),
    };

    public static bool TryFromLetter(char letter, out PermissionAction action)
    {
        switch (char.ToUpperInvariant(letter))
        {
            case 'V': action = PermissionAction.View; return true;
            case 'C': action = PermissionAction.Create; return true;
            case 'E': action = PermissionAction.Edit; return true;
            case 'A': action = PermissionAction.Approve; return true;
            case 'F': action = PermissionAction.SeeMoney; return true;
            default: action = default; return false;
        }
    }

    /// <summary>
    /// Parses one action name sent by a client, ignoring case. Only the exact names are accepted:
    /// not numbers, and not comma-separated lists (which <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// would silently combine into a different action).
    /// </summary>
    public static bool TryParse(string? value, out PermissionAction action)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(value?.Trim(), candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                action = candidate;
                return true;
            }
        }

        action = default;
        return false;
    }
}
