using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;

namespace AlMotaheda.Modules.Fleet.Security;

/// <summary>The signed-in caller, as read from the access token.</summary>
/// <remarks>
/// Permissions in the token can be up to 15 minutes old (accepted in the Login design). Which projects the caller
/// may see is not in the token: it is asked from the Projects module on every request.
/// </remarks>
internal sealed class FleetCaller
{
    private FleetCaller(ClaimsPrincipal principal, int userId, string role)
    {
        Principal = principal;
        UserId = userId;
        Role = role;
    }

    public ClaimsPrincipal Principal { get; }

    public int UserId { get; }

    /// <summary>One of <see cref="AccessRoles"/>.</summary>
    public string Role { get; }

    /// <summary>An action on the <c>equipment</c> permission module.</summary>
    public bool Can(PermissionAction action) => Principal.HasPermission(FleetPermissions.Module, action);

    /// <summary>Recording or fixing an equipment cost: equipment Create or equipment Edit (05-fleet.md, section 4).</summary>
    public bool CanRecordCosts => Can(PermissionAction.Create) || Can(PermissionAction.Edit);

    /// <summary>Rates and amounts are shown only with equipment See money.</summary>
    public bool SeesMoney => Can(PermissionAction.SeeMoney);

    /// <summary>The caller, or <c>null</c> for a token without a user id or role (never issued by Access).</summary>
    public static FleetCaller? From(ClaimsPrincipal principal) =>
        principal.GetUserId() is { } userId && principal.GetRoleCode() is { Length: > 0 } role
            ? new FleetCaller(principal, userId, role)
            : null;
}

/// <summary>The permission modules this module checks.</summary>
internal static class FleetPermissions
{
    /// <summary>The register, assignments and equipment costs (View, Create, Edit, Approve, See money).</summary>
    public const string Module = "equipment";

    /// <summary>Recording (Create) and voiding (Approve) the payment of a credit cost (decision G4).</summary>
    public const string PaymentsModule = "payments";
}
