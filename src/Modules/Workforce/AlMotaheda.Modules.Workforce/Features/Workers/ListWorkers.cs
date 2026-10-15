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

namespace AlMotaheda.Modules.Workforce.Features.Workers;

/// <summary>
/// <c>GET /api/workers</c>: one page of the company's workers, by English name. National ids are masked except
/// the last 4 digits unless the caller has workers Edit; rates only with workers See money.
/// </summary>
/// <remarks>
/// Filtering by <c>projectId</c> says who works on a project, so the project must be visible to the caller
/// (404 otherwise, like every project endpoint).
/// </remarks>
internal static class ListWorkers
{
    internal sealed record Response(IReadOnlyList<WorkerView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/workers", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .WithName("ListWorkers")
            .WithSummary("List the company's workers (search, tradeId, status, projectId = assigned there now; paging)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        CancellationToken cancellationToken,
        string? search = null,
        int? tradeId = null,
        string? status = null,
        int? projectId = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);

        if (tradeId is < 1)
        {
            errors.Add("tradeId", "tradeId is not valid.");
        }

        if (projectId is < 1)
        {
            errors.Add("projectId", "projectId is not valid.");
        }

        WorkerStatus? statusFilter = null;
        if (Input.Clean(status) is { } statusText)
        {
            if (Input.TryParseName<WorkerStatus>(statusText, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add("status", $"status must be one of: {string.Join(", ", Enum.GetNames<WorkerStatus>())}.");
            }
        }

        var searchText = Input.Clean(search);
        if (searchText is { Length: > 100 })
        {
            errors.Add("search", "search must be at most 100 characters.");
        }

        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        if (projectId is { } wanted && await access.FindVisibleAsync(httpContext.User, wanted, cancellationToken) is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        var fullNationalId = caller.Can(PermissionAction.Edit);
        var filter = new WorkerFilter(searchText, tradeId, statusFilter, projectId, SearchNationalId: fullNationalId);

        var query = WorkerQueries.Search(db, filter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(
            rows.Select(row => WorkerView.From(row, fullNationalId, caller.SeesMoney)).ToList(),
            page,
            pageSize,
            totalCount));
    }
}
