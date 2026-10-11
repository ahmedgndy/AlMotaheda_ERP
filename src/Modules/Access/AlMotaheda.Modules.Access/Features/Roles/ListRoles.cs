using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Access.Features.Roles;

/// <summary>
/// <c>GET /api/roles</c>: the six roles with their current permissions, plus the module and action lists the
/// matrix needs to draw its rows and columns.
/// </summary>
internal static class ListRoles
{
    internal sealed record Response(IReadOnlyList<string> Modules, IReadOnlyList<string> Actions, IReadOnlyList<RoleView> Roles);

    public static void Map(IEndpointRouteBuilder roles) =>
        roles.MapGet("/", HandleAsync)
            .RequirePermission(PermissionCatalog.AccountsModule, PermissionAction.View)
            .WithName("ListRoles")
            .WithSummary("Roles and their permissions");

    internal static async Task<Ok<Response>> HandleAsync(AccessDbContext db, CancellationToken cancellationToken)
    {
        var roles = await RoleView.LoadAsync(db, code: null, cancellationToken);

        return TypedResults.Ok(new Response(
            PermissionCatalog.Modules,
            PermissionActions.All.Select(action => action.ToString()).ToList(),
            roles));
    }
}
