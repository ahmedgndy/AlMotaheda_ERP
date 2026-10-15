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

namespace AlMotaheda.Modules.Workforce.Features.Trades;

/// <summary><c>GET /api/trades</c>: the company's trades, by English name. Small list, no paging.</summary>
internal static class ListTrades
{
    internal sealed record Response(IReadOnlyList<TradeView> Items);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/trades", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .WithName("ListTrades")
            .WithSummary("List the worker trades (activeOnly). Default rates only with workers See money");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        WorkforceDbContext db,
        CancellationToken cancellationToken,
        bool activeOnly = false)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var trades = await TradeQueries.List(db, activeOnly).ToListAsync(cancellationToken);
        return TypedResults.Ok(new Response(trades.Select(trade => TradeView.From(trade, caller.SeesMoney)).ToList()));
    }
}
