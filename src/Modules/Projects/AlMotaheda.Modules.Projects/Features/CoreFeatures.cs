using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Projects.Features;

/// <summary>
/// Wires the first part of the project workspace: suppliers, project items, expenses and supplies, and supplier
/// payments. See docs/project-workspace/01-projects-core.md.
/// </summary>
internal static class CoreFeatures
{
    public static void AddServices(IServiceCollection services)
    {
    }

    /// <param name="api">The shared <c>/api</c> group, for company-wide routes such as <c>/suppliers</c>.</param>
    /// <param name="projects">The <c>/api/projects</c> group, for routes under one project.</param>
    public static void Map(IEndpointRouteBuilder api, IEndpointRouteBuilder projects)
    {
    }
}
