using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;

namespace AlMotaheda.Modules.HR.Common;

/// <summary>The permission modules this module checks.</summary>
internal static class HrPermissions
{
    /// <summary>Employees, documents, salaries and salary sheets (View, Create, Edit, Approve, See money).</summary>
    public const string Module = "hr";

    /// <summary>Asking Finance to pay an approved salary sheet needs payments Create.</summary>
    public const string Payments = "payments";
}

/// <summary>The signed-in caller, as read from the access token.</summary>
internal sealed class HrCaller
{
    private HrCaller(ClaimsPrincipal principal, int userId)
    {
        Principal = principal;
        UserId = userId;
    }

    public ClaimsPrincipal Principal { get; }

    public int UserId { get; }

    public bool Can(PermissionAction action) => Principal.HasPermission(HrPermissions.Module, action);

    /// <summary>Salaries, payroll amounts and bank details are returned only with hr See money; otherwise <c>null</c>.</summary>
    public bool SeesMoney => Can(PermissionAction.SeeMoney);

    public static HrCaller? From(ClaimsPrincipal principal) =>
        principal.GetUserId() is { } userId ? new HrCaller(principal, userId) : null;
}

/// <summary>A user shown next to a record: who prepared or reviewed it.</summary>
internal sealed record HrPersonRef(int UserId, string NameAr, string NameEn, bool IsActive)
{
    public static HrPersonRef? From(int? userId, IReadOnlyDictionary<int, UserSummary> users) =>
        userId is not { } id
            ? null
            : users.TryGetValue(id, out var user)
                ? new HrPersonRef(user.Id, user.NameAr, user.NameEn, user.IsActive)
                : new HrPersonRef(id, $"#{id}", $"#{id}", false);
}
