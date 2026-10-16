using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace AlMotaheda.Modules.Workforce.Features.Trades;

/// <summary><c>GET /api/trades</c>: the company's trades, by English name. Small list, no paging.</summary>
/// <remarks>
/// Cached (<c>workforce:trades:{all|active}:v1</c>), removed whenever a trade changes. The daily rates are hidden
/// after reading, per caller, as before; sheets read a trade's rate from the database, never from this cache.
/// </remarks>
internal static class ListTrades
{
    internal sealed record Response(IReadOnlyList<TradeView> Items);

    private static readonly HybridCacheEntryOptions Lifetime = new() { Expiration = TimeSpan.FromMinutes(30), LocalCacheExpiration = TimeSpan.FromMinutes(10) };

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/trades", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .WithName("ListTrades")
            .WithSummary("List the worker trades (activeOnly). Default rates only with workers See money");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        WorkforceDbContext db,
        HybridCache cache,
        CancellationToken cancellationToken,
        bool activeOnly = false)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var trades = await cache.GetOrCreateAsync(
            $"workforce:trades:{(activeOnly ? "active" : "all")}:v1",
            (db, activeOnly),
            static async (state, token) => await TradeQueries.List(state.db, state.activeOnly).ToListAsync(token),
            Lifetime,
            [CacheTags.Aggregate("Trade")],
            cancellationToken);
        return TypedResults.Ok(new Response(trades.Select(trade => TradeView.From(trade, caller.SeesMoney)).ToList()));
    }
}
