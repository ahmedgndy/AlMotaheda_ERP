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
    }

    /// <param name="api">The shared <c>/api</c> group.</param>
    /// <param name="projects">The <c>/api/projects</c> group, for routes under one project.</param>
    public static void Map(IEndpointRouteBuilder api, IEndpointRouteBuilder projects)
    {
    }
}
