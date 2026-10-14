using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
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

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>
/// <c>GET /api/projects/{pid}/expenses/{id}</c>: one supply or site expense with its payment allocations.
/// Without projects SeeMoney only the recorder may open it; anyone else gets the same 404 as for a record that
/// doesn't exist.
/// </summary>
internal static class GetExpense
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/expenses/{expenseId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("GetProjectExpense")
            .WithSummary("One supply or site expense of a project, with its payments");

    internal static async Task<Results<Ok<ExpenseView>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int expenseId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var row = await ExpenseQueries.One(db, projectId, expenseId).SingleOrDefaultAsync(cancellationToken);
        if (row is null || (!caller.Can(PermissionAction.SeeMoney) && row.CreatedBy != caller.UserId))
        {
            return CoreProblems.ExpenseNotFound();
        }

        return TypedResults.Ok((await ExpenseView.FromAsync([row], users, cancellationToken))[0]);
    }
}
