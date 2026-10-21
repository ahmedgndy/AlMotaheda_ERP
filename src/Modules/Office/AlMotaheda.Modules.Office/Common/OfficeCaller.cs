using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Office.Data;

namespace AlMotaheda.Modules.Office.Common;

/// <summary>The permission module this module checks.</summary>
internal static class OfficePermissions
{
    /// <summary>
    /// Office records. View and Create: the custodian (Office Admin) on the custodies they hold. Approve: reviewers
    /// (Accountant, Executive), who see every custody. See money: finance (Accountant, Executive).
    /// </summary>
    public const string Module = "office";

    /// <summary>Agreeing a funding request asks Finance to pay it, so it also needs payments Create (the Accountant).</summary>
    public const string Payments = "payments";
}

/// <summary>The signed-in caller, as read from the access token.</summary>
internal sealed class OfficeCaller
{
    private OfficeCaller(ClaimsPrincipal principal, int userId)
    {
        Principal = principal;
        UserId = userId;
        RoleCode = principal.GetRoleCode() ?? "";
    }

    public ClaimsPrincipal Principal { get; }

    public int UserId { get; }

    public string RoleCode { get; }

    public bool Can(PermissionAction action) => Principal.HasPermission(OfficePermissions.Module, action);

    /// <summary>A reviewer: approves, returns and rejects office records of other people.</summary>
    public bool IsReviewer => Can(PermissionAction.Approve);

    /// <summary>The Executive: the second approver of high-risk expenses.</summary>
    public bool IsExecutive => RoleCode == AccessRoles.Executive;

    /// <summary>Reviewers and finance see every custody; anyone else only the custodies they hold.</summary>
    public bool SeesAll => IsReviewer || Can(PermissionAction.SeeMoney);

    public bool MaySee(OfficeCustody custody) => SeesAll || custody.HolderUserId == UserId;

    public bool MaySee(int holderUserId) => SeesAll || holderUserId == UserId;

    /// <summary>Only the holder records expenses, requests and counts on their custody.</summary>
    public bool Holds(OfficeCustody custody) => custody.HolderUserId == UserId;

    public static OfficeCaller? From(ClaimsPrincipal principal) =>
        principal.GetUserId() is { } userId ? new OfficeCaller(principal, userId) : null;
}

/// <summary>A user shown next to a record: who recorded, reviewed or holds it.</summary>
internal sealed record OfficePersonRef(int UserId, string NameAr, string NameEn, bool IsActive)
{
    public static OfficePersonRef? From(int? userId, IReadOnlyDictionary<int, UserSummary> users) =>
        userId is not { } id
            ? null
            : users.TryGetValue(id, out var user)
                ? new OfficePersonRef(user.Id, user.NameAr, user.NameEn, user.IsActive)
                : new OfficePersonRef(id, $"#{id}", $"#{id}", false);
}
