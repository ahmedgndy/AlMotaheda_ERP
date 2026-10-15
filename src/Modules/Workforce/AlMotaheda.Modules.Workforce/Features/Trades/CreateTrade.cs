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

/// <summary><c>POST /api/trades</c>: add a trade to the company list.</summary>
internal static class CreateTrade
{
    internal sealed record Request(string? Code, string? NameAr, string? NameEn, decimal? DefaultDailyRate, string? DefaultWorkType)
        : ITradeFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/trades", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Edit)
            .WithName("CreateTrade")
            .WithSummary("Add a worker trade with its default daily rate and work type");

    internal static async Task<Results<Created<TradeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        WorkforceDbContext db,
        WorkforceLog log,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = TradeForm.Validate(request, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        if (await db.Trades.AnyAsync(trade => trade.Code == fields!.Code, cancellationToken))
        {
            return WorkforceProblems.CodeTaken();
        }

        var created = new Trade { IsActive = true };
        fields!.ApplyTo(created);
        db.Trades.Add(created);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, TradeConfiguration.CodeIndex))
        {
            return WorkforceProblems.CodeTaken();
        }

        log.TradeCreated(caller.UserId, created.Id);
        return TypedResults.Created($"/api/trades/{created.Id}", TradeView.From(created, caller.SeesMoney));
    }
}
