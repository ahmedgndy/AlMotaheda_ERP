using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Budgets;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Features.Reports;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Overview;

/// <summary>
/// <c>GET /api/projects/{pid}/overview</c>: the money picture of one project. The approved budget, and per cost
/// group the recognized cost, paid, outstanding, pending review and committed amounts, each in its own field.
/// Plus the project items totals, the cost not linked to an item yet, and a few counts.
/// </summary>
/// <remarks>
/// Read-only. Labor, subcontract and equipment figures come only from the <see cref="IProjectCostSource"/> of the
/// modules that own them; a module that is not installed gives zero and <c>hasSource = false</c>.
/// </remarks>
internal static class GetProjectOverview
{
    internal sealed record OverviewBudget(int Id, int Version, string? Title, DateTime? ApprovedAt);

    /// <param name="Totals">The sums over every project item, active and inactive.</param>
    /// <param name="NotLinked">Approved records with no item yet. Never inside <paramref name="Totals"/>.</param>
    internal sealed record OverviewItems(ItemsGroupTotals Totals, ItemsGroupTotals NotLinked);

    /// <param name="ReturnedRecords">Expenses and supplies returned to their recorder and not fixed yet.</param>
    /// <param name="PendingReviewRecords">Records of every group waiting for review.</param>
    internal sealed record OverviewCounts(int ActiveItems, int ReturnedRecords, int PendingReviewRecords);

    internal sealed record Response(
        OverviewBudget? Budget,
        IReadOnlyList<OverviewGroup> Groups,
        OverviewTotals Totals,
        OverviewItems Items,
        OverviewCounts Counts);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/overview", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetProjectOverview")
            .WithSummary("The project's approved budget against its recognized, paid, outstanding, pending and committed cost, with items totals and counts");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        ProjectCostLoader costs,
        IProjectItemsSummary itemsSummary,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var header = await BudgetQueries.Approved(db, projectId).SingleOrDefaultAsync(cancellationToken);
        IReadOnlyList<BudgetLineAmount>? lines = header is null
            ? null
            : await BudgetQueries.Amounts(db, projectId, [header.Id]).ToListAsync(cancellationToken);

        var data = await costs.LoadAsync(projectId, from: null, to: null, cancellationToken);
        var (groups, totals) = ProjectOverview.Compute(lines, data.Groups);

        var items = await itemsSummary.SummarizeAsync(projectId, cancellationToken);
        var expenses = ExpenseSummary.Compute(data.Expenses);

        return TypedResults.Ok(new Response(
            header is null ? null : new OverviewBudget(header.Id, header.Version, header.Title, header.ApprovedAt),
            groups,
            totals,
            new OverviewItems(items.Totals, items.Unallocated),
            new OverviewCounts(items.ActiveItemCount, expenses.ReturnedCount, ProjectCosts.Total(data.Groups).PendingRecords)));
    }
}
