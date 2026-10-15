using AlMotaheda.Modules.Projects.Features.Overview;
using AlMotaheda.Modules.Projects.Features.Reports;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Projects.Features;

/// <summary>
/// Wires the read-only overview and report endpoints of a project.
/// See docs/project-workspace/02-projects-planning.md, section E.
/// </summary>
internal static class ReportingFeatures
{
    public static void AddServices(IServiceCollection services)
    {
        // Reads the database and asks the other modules' cost sources, once per request.
        services.AddScoped<ProjectCostLoader>();
    }

    /// <param name="projects">The <c>/api/projects</c> group.</param>
    public static void Map(IEndpointRouteBuilder projects)
    {
        GetProjectOverview.Map(projects);
        GetCostSummary.Map(projects);
        GetObligations.Map(projects);
    }
}
