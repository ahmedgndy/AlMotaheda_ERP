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
    }

    /// <param name="projects">The <c>/api/projects</c> group.</param>
    public static void Map(IEndpointRouteBuilder projects)
    {
    }
}
