using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Payments;

/// <summary>
/// <c>GET /api/workers/{id}/payments</c>: one page of a worker's payments with the days each one settled, newest first.
/// Only payments and allocations on projects the caller may see are shown; a payment that also settled days on a
/// hidden project shows only its visible part, and its amount is that part.
/// </summary>
internal static class ListWorkerPayments
{
    internal sealed record Response(IReadOnlyList<WorkerPaymentView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/workers/{workerId:int}/payments", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.SeeMoney)
            .WithName("ListWorkerPayments")
            .WithSummary("List a worker's payments with their allocations (includeVoided; paging)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int workerId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        CancellationToken cancellationToken,
        bool includeVoided = false,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (WorkforceCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        if (!await db.Workers.AnyAsync(worker => worker.Id == workerId, cancellationToken))
        {
            return WorkforceProblems.WorkerNotFound();
        }

        var projectIds = await WorkerPaymentQueries.PaidProjectIds(db, workerId).ToListAsync(cancellationToken);
        var visible = await ProjectVisibility.AmongAsync(access, httpContext.User, projectIds, cancellationToken);

        // A caller who sees every project of these payments gets them unfiltered; others only the visible ones.
        var seesAll = visible.Count == projectIds.Count;
        var query = WorkerPaymentQueries.OfWorker(db, workerId, includeVoided, seesAll ? null : visible.ToList());
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(
            rows.Select(row => WorkerPaymentView.From(row, seesAll ? null : visible)).ToList(), page, pageSize, totalCount));
    }
}
