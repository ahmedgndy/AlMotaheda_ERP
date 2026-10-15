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

namespace AlMotaheda.Modules.Projects.Features.Budgets;

/// <summary><c>GET /api/projects/{pid}/budgets/{id}</c>: one version with its lines.</summary>
internal static class GetBudget
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/budgets/{budgetId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetBudget")
            .WithSummary("Get one version of a project's budget with its lines");

    internal static async Task<Results<Ok<BudgetView>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int budgetId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var view = await BudgetView.LoadAsync(db, users, projectId, budgetId, cancellationToken);
        return view is null ? PlanningProblems.BudgetNotFound() : TypedResults.Ok(view);
    }
}
