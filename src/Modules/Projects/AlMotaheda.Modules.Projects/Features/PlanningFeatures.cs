using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Features.Budgets;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Features.DailyReports;
using AlMotaheda.Modules.Projects.Features.ReportRequests;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Projects.Features;

/// <summary>
/// Wires the second part of the project workspace: budgets, the price catalog, daily site reports and report
/// requests. See docs/project-workspace/02-projects-planning.md.
/// </summary>
internal static class PlanningFeatures
{
    public static void AddServices(IServiceCollection services)
    {
        // Nothing to add: the budget comparison uses the items summary and the cost sources registered by the
        // first part and by the other modules, and every other slice uses the database context directly.
        _ = services;
    }

    /// <param name="api">The shared <c>/api</c> group.</param>
    /// <param name="projects">The <c>/api/projects</c> group, for routes under one project.</param>
    public static void Map(IEndpointRouteBuilder api, IEndpointRouteBuilder projects)
    {
        _ = api;

        ListBudgets.Map(projects);
        GetBudget.Map(projects);
        CreateBudget.Map(projects);
        UpdateBudget.Map(projects);
        ApproveBudget.Map(projects);
        GetBudgetComparison.Map(projects);

        ListCatalogItems.Map(projects);
        GetCatalogItem.Map(projects);
        CreateCatalogItem.Map(projects);
        UpdateCatalogItem.Map(projects);
        RequestPriceChange.Map(projects);
        ListPriceChanges.Map(projects);
        ApprovePriceChange.Map(projects);
        RejectPriceChange.Map(projects);

        ListDailyReports.Map(projects);
        GetDailyReport.Map(projects);
        CreateDailyReport.Map(projects);
        UpdateDailyReport.Map(projects);
        ReviewDailyReport.Map(projects);
        ReturnDailyReport.Map(projects);

        ListReportRequests.Map(projects);
        GetReportRequest.Map(projects);
        CreateReportRequest.Map(projects);
        StartReportRequest.Map(projects);
        SubmitReportRequest.Map(projects);
        CompleteReportRequest.Map(projects);
        ReturnReportRequest.Map(projects);
        CancelReportRequest.Map(projects);
    }
}

/// <summary>One name lookup for the people a planning screen shows (authors, reviewers, requesters).</summary>
internal static class PlanningPeople
{
    public static async Task<IReadOnlyDictionary<int, UserSummary>> FindAsync(
        IUserDirectory users, IEnumerable<int> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<int, UserSummary>() : await users.FindAsync(ids, cancellationToken);
    }
}
