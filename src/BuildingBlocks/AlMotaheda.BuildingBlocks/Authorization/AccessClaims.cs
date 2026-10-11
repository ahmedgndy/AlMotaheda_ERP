using System.Security.Claims;

namespace AlMotaheda.BuildingBlocks.Authorization;

/// <summary>
/// The claims inside an access token. The Access module writes them; every module reads them through
/// <see cref="ClaimsPrincipalExtensions"/>. Keep the names short: they travel with every request.
/// </summary>
public static class AccessClaims
{
    /// <summary>The user's id (JWT "sub").</summary>
    public const string UserId = "sub";

    /// <summary>The user's role code, for example <c>manager</c>.</summary>
    public const string Role = "role";

    /// <summary>One claim per module the user can use, for example <c>projects:VCEF</c>.</summary>
    public const string Permission = "perm";

    /// <summary>Present (value <c>true</c>) while the user must change their password before doing anything else.</summary>
    public const string PasswordChangeRequired = "pwd_change";

    /// <summary>The sign-in this token belongs to (JWT "sid"). Lets sensitive operations check the sign-in hasn't ended.</summary>
    public const string SessionId = "sid";

    /// <summary>Formats one permission claim value: the module code, a colon, then one letter per action.</summary>
    public static string FormatPermission(string module, IEnumerable<PermissionAction> actions) =>
        module + ":" + string.Concat(actions.Distinct().Order().Select(PermissionActions.ToLetter));
}

/// <summary>
/// Reads the signed-in user from the access token. These checks need no database call.
/// </summary>
/// <remarks>
/// The token can be up to 15 minutes old. Operations that grant or remove access must re-check the
/// caller in the database as well; the Access module does that for every user-management change.
/// </remarks>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id, or <c>null</c> for an anonymous request.</summary>
    public static int? GetUserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(AccessClaims.UserId), out var id) ? id : null;

    public static string? GetRoleCode(this ClaimsPrincipal user) => user.FindFirstValue(AccessClaims.Role);

    /// <summary>The sign-in the token belongs to, or <c>null</c> when the token has none.</summary>
    public static Guid? GetSessionId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(AccessClaims.SessionId), out var id) ? id : null;

    public static bool IsPasswordChangeRequired(this ClaimsPrincipal user) =>
        user.HasClaim(AccessClaims.PasswordChangeRequired, "true");

    public static bool HasPermission(this ClaimsPrincipal user, string module, PermissionAction action)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var letter = PermissionActions.ToLetter(action);
        var prefix = module + ":";

        foreach (var claim in user.FindAll(AccessClaims.Permission))
        {
            if (claim.Value.StartsWith(prefix, StringComparison.Ordinal)
                && claim.Value.AsSpan(prefix.Length).Contains(letter))
            {
                return true;
            }
        }

        return false;
    }
}
