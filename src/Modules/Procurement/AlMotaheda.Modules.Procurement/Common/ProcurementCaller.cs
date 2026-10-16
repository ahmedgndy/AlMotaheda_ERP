using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Procurement.Common;

/// <summary>The permission modules this module checks.</summary>
internal static class ProcurementPermissions
{
    /// <summary>Requisitions (C), purchasing work (E), requisition approval (A), prices and amounts (F).</summary>
    public const string Module = "procurement";

    /// <summary>Goods receipts are recorded on site: site Create, by a member of the project.</summary>
    public const string Site = "site";

    /// <summary>Supplier invoices: recorded with finance Create, mismatches resolved with finance Approve.</summary>
    public const string Finance = "finance";
}

/// <summary>The signed-in caller as Procurement sees them.</summary>
internal sealed class ProcurementCaller
{
    private ProcurementCaller(ClaimsPrincipal principal, int userId, string role, Guid? sessionId)
    {
        Principal = principal;
        UserId = userId;
        Role = role;
        SessionId = sessionId;
    }

    public ClaimsPrincipal Principal { get; }

    public int UserId { get; }

    public string Role { get; }

    public Guid? SessionId { get; }

    public bool IsExecutive => Role == AccessRoles.Executive;

    public bool IsAccountant => Role == AccessRoles.Accountant;

    public bool Can(string module, PermissionAction action) => Principal.HasPermission(module, action);

    /// <summary>Prices and amounts are <c>null</c> without procurement See money.</summary>
    public bool SeesMoney => Can(ProcurementPermissions.Module, PermissionAction.SeeMoney);

    public static ProcurementCaller? From(ClaimsPrincipal principal) =>
        principal.GetUserId() is { } userId && principal.GetRoleCode() is { Length: > 0 } role
            ? new ProcurementCaller(principal, userId, role, principal.GetSessionId())
            : null;

    /// <summary>
    /// The Executive-only steps (quotations, the policy) re-check the role in the database: a token can be up to
    /// 15 minutes old.
    /// </summary>
    public async Task<bool> IsActiveExecutiveAsync(IExecutiveCheck check, CancellationToken cancellationToken) =>
        IsExecutive && SessionId is { } session && await check.IsActiveExecutiveAsync(UserId, session, cancellationToken);

    /// <summary>The projects whose documents the caller sees; <c>null</c> = all (Executive, Accountant, Purchasing).</summary>
    public Task<IReadOnlySet<int>?> VisibleProjectsAsync(IVisibleProjects projects, CancellationToken cancellationToken) =>
        projects.IdsAsync(Principal, cancellationToken);
}

/// <summary>A user shown next to a step of a document.</summary>
internal sealed record ProcurementPersonRef(int UserId, string NameAr, string NameEn)
{
    public static ProcurementPersonRef? From(int? userId, IReadOnlyDictionary<int, UserSummary> users) =>
        userId is not { } id
            ? null
            : users.TryGetValue(id, out var user)
                ? new ProcurementPersonRef(user.Id, user.NameAr, user.NameEn)
                : new ProcurementPersonRef(id, $"#{id}", $"#{id}");
}
