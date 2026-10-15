using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Budgets;

/// <summary>
/// <c>GET /api/projects/{pid}/budgets</c>: every version of the project's budget, newest first, each with its total
/// and its totals per cost group. Budgets are money, so this needs projects See money.
/// </summary>
internal static class ListBudgets
{
    internal sealed record Response(IReadOnlyList<BudgetView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/budgets", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("ListBudgets")
            .WithSummary("List every version of a project's budget, newest first, with totals per cost group");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var query = BudgetQueries.Headers(db, projectId);
        var totalCount = await query.CountAsync(cancellationToken);
        var headers = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = await BudgetView.FromAsync(db, users, projectId, headers, cancellationToken);

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount));
    }
}
