using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Projects.Security;

/// <summary>
/// Before any change that grants or removes access (creating a project with its manager, changing membership),
/// checks in the database that the caller is still an active Executive, still signed in with this sign-in, and has
/// no pending password change. The token alone can be 15 minutes old.
/// </summary>
internal sealed class ExecutiveGate(IExecutiveCheck executives, ProjectsLog log)
{
    /// <summary>Returns the 403 to send back, or <c>null</c> when the caller may go ahead.</summary>
    public async Task<ProblemHttpResult?> CheckAsync(ClaimsPrincipal caller, string action, CancellationToken cancellationToken)
    {
        var userId = caller.GetUserId();
        var sessionId = caller.GetSessionId();

        if (userId is null || sessionId is null
            || !await executives.IsActiveExecutiveAsync(userId.Value, sessionId.Value, cancellationToken))
        {
            log.ActionRefused(userId, action, "not a currently signed-in active Executive");
            return ProjectProblems.ExecutiveOnly();
        }

        return null;
    }
}

/// <summary>Endpoint policies of the Projects module.</summary>
internal static class ProjectPolicies
{
    /// <summary>
    /// Executive only, from the token's role claim: the fast first check. Handlers that change access then call
    /// <see cref="ExecutiveGate"/>. Built on <see cref="PermissionAuthorization.SignedInPolicy"/>, so a user who must
    /// change their password is refused here too.
    /// </summary>
    public static TBuilder RequireExecutive<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionAuthorization.SignedInPolicy()
            .RequireClaim(AccessClaims.Role, AccessRoles.Executive)
            .Build());
}
