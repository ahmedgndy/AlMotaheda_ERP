using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Data;

namespace AlMotaheda.Modules.Projects.Security;

/// <summary>The signed-in caller, as read from the access token.</summary>
/// <remarks>
/// Role and permissions in the token can be up to 15 minutes old (accepted in the Login design). Membership is not
/// in the token: it is read from the database on every request, so removing someone from a project works at once.
/// </remarks>
internal sealed class ProjectCaller
{
    private ProjectCaller(ClaimsPrincipal principal, int userId, string role)
    {
        Principal = principal;
        UserId = userId;
        Role = role;
    }

    public ClaimsPrincipal Principal { get; }

    public int UserId { get; }

    /// <summary>One of <see cref="AccessRoles"/>.</summary>
    public string Role { get; }

    public bool IsExecutive => Role == AccessRoles.Executive;

    /// <summary>True for the roles that see every project; everyone else sees only the projects they are on.</summary>
    public bool SeesAllProjects => ProjectScopes.IsCompanyWide(Role);

    public bool Can(PermissionAction action) => Principal.HasPermission(ProjectPermissions.Module, action);

    /// <summary>The caller, or <c>null</c> for a token without a user id or role (never issued by Access).</summary>
    public static ProjectCaller? From(ClaimsPrincipal principal) =>
        principal.GetUserId() is { } userId && principal.GetRoleCode() is { Length: > 0 } role
            ? new ProjectCaller(principal, userId, role)
            : null;
}

/// <summary>The permission module this feature checks: <c>projects</c> (View, Create, Edit, Approve, See money).</summary>
internal static class ProjectPermissions
{
    public const string Module = "projects";

    /// <summary>Used to read another user's project list (the Accounts screens).</summary>
    public const string AccountsModule = "accounts";

    /// <summary>Purchasing manages the same supplier list (procurement View / Edit).</summary>
    public const string ProcurementModule = "procurement";
}

/// <summary>
/// The data scope of each role. Executive, Accountant and Purchasing see every project ("company" in the prototype;
/// Purchasing serves every project but holds no <c>projects</c> permission, so this only scopes its procurement lists);
/// Manager, Engineer and Staff see only projects where they have an active membership. Office administrators
/// have no projects permission by default, so the scope never applies to them unless an Executive grants one.
/// </summary>
/// <remarks>
/// Roles are fixed (seeded, no endpoint adds roles), so the map lives here, in one place, and Access needs no
/// scope column. If roles ever become editable, the scope should move to the Access role table.
/// </remarks>
internal static class ProjectScopes
{
    public static bool IsCompanyWide(string role) => role is AccessRoles.Executive or AccessRoles.Accountant or AccessRoles.Purchasing;
}

/// <summary>Which roles may hold each responsibility (decision D6: the prototype's pickers).</summary>
internal static class ResponsibilityRoles
{
    private static readonly string[] Managers = [AccessRoles.Executive, AccessRoles.Manager];
    private static readonly string[] SiteEngineers = [AccessRoles.Engineer];
    private static readonly string[] TeamMembers = [AccessRoles.Manager, AccessRoles.Engineer, AccessRoles.Staff];

    public static IReadOnlyList<string> For(MemberResponsibility responsibility) => responsibility switch
    {
        MemberResponsibility.ProjectManager => Managers,
        MemberResponsibility.SiteEngineer => SiteEngineers,
        MemberResponsibility.TeamMember => TeamMembers,
        _ => [],
    };

    public static bool Fits(MemberResponsibility responsibility, string role) => For(responsibility).Contains(role);

    /// <summary>For error messages: "Executive or Manager".</summary>
    public static string Describe(MemberResponsibility responsibility) => responsibility switch
    {
        MemberResponsibility.ProjectManager => "an Executive or a Manager",
        MemberResponsibility.SiteEngineer => "an Engineer",
        _ => "a Manager, an Engineer or Staff",
    };
}
