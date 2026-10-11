using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Access.Features.Users;

/// <summary><c>GET /api/users/{id}</c>: one user, including lockout state and the rowVersion to send back with changes.</summary>
internal static class GetUser
{
    public static void Map(IEndpointRouteBuilder users) =>
        users.MapGet("/{id:int}", HandleAsync)
            .WithName("GetUser")
            .WithSummary("One user (accounts View, or yourself)");

    internal static async Task<Results<Ok<UserDetails>, ProblemHttpResult>> HandleAsync(
        int id,
        HttpContext httpContext,
        AccessDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // Checked before the lookup, so a caller without access can't learn which ids exist.
        if (!UserReadAccess.CanRead(httpContext.User, id))
        {
            return ApiProblem.ForbiddenResult();
        }

        var user = await UserDetails.LoadAsync(db, id, time.GetUtcNow().UtcDateTime, cancellationToken);
        return user is null ? ApiProblem.NotFoundResult("The user was not found.") : TypedResults.Ok(user);
    }
}
