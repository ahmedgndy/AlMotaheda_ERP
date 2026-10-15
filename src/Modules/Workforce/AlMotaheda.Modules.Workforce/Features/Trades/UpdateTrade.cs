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

/// <summary><c>PUT /api/trades/{id}</c>: edit a trade, or set it inactive. Approved lines keep the rate they were approved with.</summary>
internal static class UpdateTrade
{
    internal sealed record Request(
        string? Code,
        string? NameAr,
        string? NameEn,
        decimal? DefaultDailyRate,
        string? DefaultWorkType,
        bool? IsActive,
        string? RowVersion) : ITradeFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/trades/{tradeId:int}", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Edit)
            .WithName("UpdateTrade")
            .WithSummary("Edit a worker trade or set it inactive (approved labor keeps its rates)");

    internal static async Task<Results<Ok<TradeView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int tradeId,
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
        if (request.IsActive is null)
        {
            errors.Add("isActive", "isActive is required.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        var trade = await db.Trades.SingleOrDefaultAsync(candidate => candidate.Id == tradeId, cancellationToken);
        if (trade is null)
        {
            return WorkforceProblems.TradeNotFound();
        }

        if (fields!.Code != trade.Code
            && await db.Trades.AnyAsync(other => other.Id != tradeId && other.Code == fields.Code, cancellationToken))
        {
            return WorkforceProblems.CodeTaken();
        }

        fields.ApplyTo(trade);
        trade.IsActive = request.IsActive!.Value;

        // Saves only if nobody changed the trade since the caller read it.
        db.Entry(trade).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, TradeConfiguration.CodeIndex))
        {
            return WorkforceProblems.CodeTaken();
        }

        log.TradeUpdated(caller.UserId, trade.Id, trade.IsActive);
        return TypedResults.Ok(TradeView.From(trade, caller.SeesMoney));
    }
}
