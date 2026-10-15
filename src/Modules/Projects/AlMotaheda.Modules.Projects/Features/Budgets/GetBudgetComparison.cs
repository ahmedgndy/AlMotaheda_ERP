using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Features.Reports;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Budgets;

/// <summary>
/// <c>GET /api/projects/{pid}/budget-comparison</c>: the approved budget (or the version named by
/// <c>budgetId</c>) against the recognized cost of the project, per cost group, in total and per project item.
/// </summary>
/// <remarks>
/// <para>Recognized cost comes from the source records only: <c>Materials</c>, <c>SiteExpenses</c> and
/// <c>Other</c> from this module's approved expenses; <c>Labor</c>, <c>Subcontractors</c> and <c>Equipment</c> from
/// the <see cref="IProjectCostSource"/> registered for that group. A group with no source is zero.</para>
/// <para>Without any budget the comparison still answers, with nothing budgeted, so the actual side shows.</para>
/// </remarks>
internal static class GetBudgetComparison
{
    /// <summary>The version compared, or <c>null</c> when the project has no approved budget and none was named.</summary>
    internal sealed record ComparedBudget(int Id, int Version, string Status, string? Title, DateTime? ApprovedAt);

    internal sealed record Response(
        ComparedBudget? Budget,
        IReadOnlyList<GroupComparison> Groups,
        ComparisonTotals Totals,
        IReadOnlyList<ItemComparison> Items);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/budget-comparison", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetBudgetComparison")
            .WithSummary("Compare the approved budget (or budgetId) with the recognized cost, per cost group and per project item");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IProjectItemsSummary itemsSummary,
        ProjectCostLoader costs,
        CancellationToken cancellationToken,
        int? budgetId = null)
    {
        var errors = new ValidationErrors();
        if (budgetId is < 1)
        {
            errors.Add("budgetId", "budgetId is not valid.");
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var header = budgetId is { } id
            ? await BudgetQueries.One(db, projectId, id).SingleOrDefaultAsync(cancellationToken)
            : await BudgetQueries.Approved(db, projectId).SingleOrDefaultAsync(cancellationToken);
        if (budgetId is not null && header is null)
        {
            return PlanningProblems.BudgetNotFound();
        }

        IReadOnlyList<BudgetLineAmount> lines = header is null
            ? []
            : await BudgetQueries.Amounts(db, projectId, [header.Id]).ToListAsync(cancellationToken);

        // The same loader as the overview and the cost summary: this module's records, then the other modules' sources.
        var actuals = (await costs.LoadAsync(projectId, from: null, to: null, cancellationToken)).Groups.Select(group => group.ToActuals());

        var (groups, totals) = BudgetComparison.ByGroup(lines, actuals);
        var items = BudgetComparison.ByItem(lines, (await itemsSummary.SummarizeAsync(projectId, cancellationToken)).Items);

        var compared = header is null
            ? null
            : new ComparedBudget(header.Id, header.Version, header.Status.ToString(), header.Title, header.ApprovedAt);

        return TypedResults.Ok(new Response(compared, groups, totals, items));
    }
}
