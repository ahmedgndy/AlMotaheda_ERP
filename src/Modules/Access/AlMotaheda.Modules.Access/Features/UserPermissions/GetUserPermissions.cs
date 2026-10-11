using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using AlMotaheda.Modules.Access.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Access.Features.UserPermissions;

/// <summary><c>GET /api/users/{id}/permissions</c>: what a user may do, and which modules are customised for them.</summary>
internal static class GetUserPermissions
{
    public static void Map(IEndpointRouteBuilder users) =>
        users.MapGet("/{id:int}/permissions", HandleAsync)
            .WithName("GetUserPermissions")
            .WithSummary("A user's effective permissions (accounts View, or yourself)");

    internal static async Task<Results<Ok<UserPermissionsView>, ProblemHttpResult>> HandleAsync(
        int id,
        HttpContext httpContext,
        AccessDbContext db,
        PermissionResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!UserReadAccess.CanRead(httpContext.User, id))
        {
            return ApiProblem.ForbiddenResult();
        }

        var view = await UserPermissionsView.LoadAsync(db, resolver, id, cancellationToken);
        return view is null ? ApiProblem.NotFoundResult("The user was not found.") : TypedResults.Ok(view);
    }
}
