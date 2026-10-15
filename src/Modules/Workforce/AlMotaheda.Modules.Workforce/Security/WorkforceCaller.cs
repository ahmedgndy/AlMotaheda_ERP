using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;

namespace AlMotaheda.Modules.Workforce.Security;

/// <summary>The permission modules this module checks: <c>workers</c>, and <c>payments</c> for worker payments (decision G4).</summary>
internal static class WorkforcePermissions
{
    public const string Workers = "workers";

    /// <summary>Recording a payment needs payments Create; voiding one needs payments Approve.</summary>
    public const string Payments = "payments";
}

/// <summary>The signed-in caller, as read from the access token.</summary>
/// <remarks>
/// Permissions in the token can be up to 15 minutes old (accepted in the Login design). Which projects the caller
/// may see is never in the token: it is asked of the Projects module on every request.
/// </remarks>
internal sealed class WorkforceCaller
{
    private WorkforceCaller(ClaimsPrincipal principal, int userId)
    {
        Principal = principal;
        UserId = userId;
    }

    public ClaimsPrincipal Principal { get; }

    public int UserId { get; }

    /// <summary>True when the token grants <paramref name="action"/> on <c>workers</c>.</summary>
    public bool Can(PermissionAction action) => Principal.HasPermission(WorkforcePermissions.Workers, action);

    /// <summary>Money fields are returned as <c>null</c> without workers See money.</summary>
    public bool SeesMoney => Can(PermissionAction.SeeMoney);

    /// <summary>The caller, or <c>null</c> for a token without a user id (never issued by Access).</summary>
    public static WorkforceCaller? From(ClaimsPrincipal principal) =>
        principal.GetUserId() is { } userId ? new WorkforceCaller(principal, userId) : null;
}
