using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Builder;

namespace AlMotaheda.Modules.Access.Common;

/// <summary>
/// Marks an endpoint as Executive-only. This is the fast first check, from the token's role claim.
/// The handler then re-checks the caller in the database through <c>UserManagementGate</c>, because a token
/// can be up to 15 minutes old.
/// </summary>
/// <remarks>
/// This is a role check, not a grantable permission: whoever could edit permissions could give themselves
/// everything, so no permission a user can be given unlocks user management.
/// </remarks>
internal static class ExecutiveOnly
{
    public static TBuilder RequireExecutive<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionAuthorization.SignedInPolicy()
            .RequireClaim(AccessClaims.Role, RoleCodes.Executive)
            .Build());
}
