using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Projects.Security;

/// <summary>
/// Before a decision that changes what money or prices count (deciding a price change or a price variance), checks in
/// the database that the caller still holds the permission and is still signed in. The token alone can be 15 minutes
/// old.
/// </summary>
internal sealed class PermissionGate(IPermissionCheck permissions, ProjectsLog log)
{
    /// <summary>Returns the 403 to send back, or <c>null</c> when the caller may go ahead.</summary>
    public async Task<ProblemHttpResult?> CheckAsync(
        ClaimsPrincipal caller, string module, PermissionAction action, string actionName, CancellationToken cancellationToken)
    {
        var userId = caller.GetUserId();
        var sessionId = caller.GetSessionId();

        if (userId is null || sessionId is null
            || !await permissions.HoldsAsync(userId.Value, sessionId.Value, module, action.ToString(), cancellationToken))
        {
            log.ActionRefused(userId, actionName, $"{module} {action} no longer held");
            return ProjectProblems.PermissionRevoked();
        }

        return null;
    }
}
