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
/// <c>PUT /api/workers/{id}</c>: edit a worker or set them inactive. A new rate or trade applies to days approved
/// from now on; approved lines keep the rate they were approved with.
/// </summary>
internal static class UpdateWorker
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? NationalId,
        string? Phone,
        int? TradeId,
        decimal? DailyRate,
        string? Notes,
        string? Status,
        string? RowVersion) : IWorkerFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/workers/{workerId:int}", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Edit)
            .WithName("UpdateWorker")
            .WithSummary("Edit a worker or set them inactive (approved labor keeps its rates)");

    internal static async Task<Results<Ok<WorkerView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int workerId,
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

        if (!Input.TryParseName<WorkerStatus>(request.Status, out var status))
        {
            errors.Add("status", $"status must be one of: {string.Join(", ", Enum.GetNames<WorkerStatus>())}.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        var worker = await db.Workers.SingleOrDefaultAsync(candidate => candidate.Id == workerId, cancellationToken);
        if (worker is null)
        {
            return WorkforceProblems.WorkerNotFound();
        }

        // Keeping an inactive trade is fine (history); moving a worker to an inactive trade is not.
        if (fields!.TradeId != worker.TradeId)
        {
            var trade = await db.Trades.AsNoTracking()
                .Where(candidate => candidate.Id == fields.TradeId)
                .Select(candidate => new { candidate.IsActive })
                .SingleOrDefaultAsync(cancellationToken);
            if (trade is null || !trade.IsActive)
            {
                errors.Add("tradeId", trade is null ? "The trade was not found." : "The trade is inactive.");
                return WorkforceProblems.Validation(errors);
            }
        }

        if (fields.NationalId is { } nationalId
            && nationalId != worker.NationalId
            && await db.Workers.AnyAsync(other => other.Id != workerId && other.NationalId == nationalId, cancellationToken))
        {
            return WorkforceProblems.NationalIdUsed();
        }

        var oldStatus = worker.Status;
        fields.ApplyTo(worker);
        worker.Status = status;
        worker.UpdatedAt = clock.UtcNow;
        worker.UpdatedBy = caller.UserId;

        // Saves only if nobody changed the worker since the caller read it.
        db.Entry(worker).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, WorkerConfiguration.NationalIdIndex))
        {
            return WorkforceProblems.NationalIdUsed();
        }

        log.WorkerUpdated(caller.UserId, worker.Id, oldStatus.ToString(), worker.Status.ToString());

        var row = await WorkerQueries.One(db, worker.Id).SingleAsync(cancellationToken);
        return TypedResults.Ok(WorkerView.From(row, fullNationalId: true, caller.SeesMoney));
    }
}
