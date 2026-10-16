using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Hybrid;

namespace AlMotaheda.Modules.Access.Features.Roles;

/// <summary>
/// <c>GET /api/roles</c>: the roles with their current permissions, plus the module and action lists the
/// matrix needs to draw its rows and columns.
/// </summary>
/// <remarks>
/// Cached (<c>access:roles:all:v1</c>): removed whenever a role or its permissions change. Only this screen reads
/// it: the permission re-check before money steps reads the database (<c>IPermissionCheck</c>), never the cache.
/// </remarks>
internal static class ListRoles
{
    internal sealed record Response(IReadOnlyList<string> Modules, IReadOnlyList<string> Actions, IReadOnlyList<RoleView> Roles);

    public static void Map(IEndpointRouteBuilder roles) =>
        roles.MapGet("/", HandleAsync)
            .RequirePermission(PermissionCatalog.AccountsModule, PermissionAction.View)
            .WithName("ListRoles")
            .WithSummary("Roles and their permissions");

    internal const string CacheKey = "access:roles:all:v1";

    private static readonly HybridCacheEntryOptions Lifetime = new() { Expiration = TimeSpan.FromMinutes(5), LocalCacheExpiration = TimeSpan.FromMinutes(1) };

    internal static async Task<Ok<Response>> HandleAsync(AccessDbContext db, HybridCache cache, CancellationToken cancellationToken)
    {
        var roles = await cache.GetOrCreateAsync(
            CacheKey,
            db,
            static async (context, token) => await RoleView.LoadAsync(context, code: null, token),
            Lifetime,
            [CacheTags.Aggregate("Role"), CacheTags.Aggregate("RolePermission")],
            cancellationToken);

        return TypedResults.Ok(new Response(
            PermissionCatalog.Modules,
            PermissionActions.All.Select(action => action.ToString()).ToList(),
            roles));
    }
}
