using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Finance.Security;

/// <summary>The permission modules Finance uses.</summary>
internal static class FinancePermissions
{
    /// <summary>Creating (Create), deciding (Approve) and seeing the amounts (See money) of payment requests.</summary>
    public const string Payments = "payments";

    /// <summary>The bank step and the ledger.</summary>
    public const string Finance = "finance";
}

/// <summary>The signed-in caller as Finance sees them.</summary>
internal sealed class FinanceCaller
{
    private readonly ClaimsPrincipal _principal;

    private FinanceCaller(ClaimsPrincipal principal, int userId, Guid? sessionId)
    {
        _principal = principal;
        UserId = userId;
        SessionId = sessionId;
    }

    public int UserId { get; }

    public Guid? SessionId { get; }

    public bool IsExecutive => _principal.GetRoleCode() == AccessRoles.Executive;

    public bool Can(string module, PermissionAction action) => _principal.HasPermission(module, action);

    public bool SeesAmounts => Can(FinancePermissions.Payments, PermissionAction.SeeMoney);

    public static FinanceCaller? From(ClaimsPrincipal principal) =>
        principal.GetUserId() is { } userId ? new FinanceCaller(principal, userId, principal.GetSessionId()) : null;

    /// <summary>
    /// The database re-check before moving money (refactor plan M8): the token may be up to 15 minutes old, so a
    /// demoted or disabled user, or one whose sign-in ended, is refused here.
    /// </summary>
    public async Task<bool> StillHoldsAsync(IPermissionCheck check, string module, PermissionAction action, CancellationToken cancellationToken) =>
        SessionId is { } session && await check.HoldsAsync(UserId, session, module, action.ToString(), cancellationToken);

    /// <summary>
    /// May the caller see a request on <paramref name="projectId"/>? Company-wide roles see every request; others
    /// see requests of their projects, and never requests that span projects.
    /// </summary>
    public static bool Sees(IReadOnlySet<int>? visibleProjects, int? projectId) =>
        visibleProjects is null || (projectId is { } id && visibleProjects.Contains(id));

    public Task<IReadOnlySet<int>?> VisibleProjectsAsync(IVisibleProjects projects, CancellationToken cancellationToken) =>
        projects.IdsAsync(_principal, cancellationToken);
}
