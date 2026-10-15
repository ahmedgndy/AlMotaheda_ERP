using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;

namespace AlMotaheda.Modules.Subcontractors.Common;

/// <summary>The permission modules this module checks.</summary>
internal static class SubPermissions
{
    /// <summary>Companies, contracts, items, measured work and manpower (View, Create, Edit, Approve, See money).</summary>
    public const string Module = "subcontractors";

    /// <summary>Recording a payment needs Create, voiding one needs Approve (decision G4).</summary>
    public const string Payments = "payments";
}

/// <summary>The signed-in caller, as read from the access token.</summary>
/// <remarks>
/// Permissions in the token can be up to 15 minutes old (accepted in the Login design). Which projects the caller
/// may open is not in the token: it is asked from Projects on every request (<c>IProjectAccess</c>).
/// </remarks>
internal sealed class SubCaller
{
    private SubCaller(ClaimsPrincipal principal, int userId, string role)
    {
        Principal = principal;
        UserId = userId;
        Role = role;
    }

    public ClaimsPrincipal Principal { get; }

    public int UserId { get; }

    /// <summary>One of <see cref="AccessRoles"/>.</summary>
    public string Role { get; }

    public bool Can(PermissionAction action) => Principal.HasPermission(SubPermissions.Module, action);

    /// <summary>Money fields are returned only with subcontractors See money; otherwise they are <c>null</c>.</summary>
    public bool SeesMoney => Can(PermissionAction.SeeMoney);

    /// <summary>The caller, or <c>null</c> for a token without a user id or role (never issued by Access).</summary>
    public static SubCaller? From(ClaimsPrincipal principal) =>
        principal.GetUserId() is { } userId && principal.GetRoleCode() is { Length: > 0 } role
            ? new SubCaller(principal, userId, role)
            : null;
}

/// <summary>A user shown next to a record: who recorded it, who reviewed it. The same shape as the Projects module's.</summary>
internal sealed record SubPersonRef(int UserId, string NameAr, string NameEn, bool IsActive)
{
    public static SubPersonRef From(int userId, IReadOnlyDictionary<int, UserSummary> users) =>
        users.TryGetValue(userId, out var user)
            ? new SubPersonRef(user.Id, user.NameAr, user.NameEn, user.IsActive)
            // Not expected: users are never deleted.
            : new SubPersonRef(userId, $"#{userId}", $"#{userId}", false);

    public static SubPersonRef? From(int? userId, IReadOnlyDictionary<int, UserSummary> users) =>
        userId is { } id ? From(id, users) : null;
}
