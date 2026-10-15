using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Workers;

/// <summary>
/// <c>POST /api/workers</c>: add a worker to the company list, once. The same person on another project reuses this
/// row through an assignment; a second row with the same national id answers 409 <c>national_id_taken</c>.
/// </summary>
internal static class CreateWorker
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? NationalId,
        string? Phone,
        int? TradeId,
        decimal? DailyRate,
        string? Notes) : IWorkerFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/workers", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Edit)
            .WithName("CreateWorker")
            .WithSummary("Add a daily worker to the company list (national id unique)");

    internal static async Task<Results<Created<WorkerView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        WorkforceDbContext db,
        CompanyClock clock,
        WorkforceLog log,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = WorkerForm.Validate(request, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        var trade = await db.Trades.AsNoTracking()
            .Where(candidate => candidate.Id == fields!.TradeId)
            .Select(candidate => new { candidate.IsActive })
            .SingleOrDefaultAsync(cancellationToken);
        if (trade is null || !trade.IsActive)
        {
            errors.Add("tradeId", trade is null ? "The trade was not found." : "The trade is inactive.");
            return WorkforceProblems.Validation(errors);
        }

        if (fields!.NationalId is { } nationalId && await db.Workers.AnyAsync(worker => worker.NationalId == nationalId, cancellationToken))
        {
            return WorkforceProblems.NationalIdUsed();
        }

        var worker = new Worker
        {
            Status = WorkerStatus.Active,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        fields.ApplyTo(worker);
        db.Workers.Add(worker);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, WorkerConfiguration.NationalIdIndex))
        {
            return WorkforceProblems.NationalIdUsed();
        }

        log.WorkerCreated(caller.UserId, worker.Id);

        var row = await WorkerQueries.One(db, worker.Id).SingleAsync(cancellationToken);
        return TypedResults.Created($"/api/workers/{worker.Id}", WorkerView.From(row, fullNationalId: true, caller.SeesMoney));
    }
}
