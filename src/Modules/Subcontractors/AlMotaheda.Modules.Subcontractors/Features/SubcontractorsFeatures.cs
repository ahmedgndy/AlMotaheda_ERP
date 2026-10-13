using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Subcontractors.Features;

/// <summary>
/// Wires the Subcontractors module's features: one line per use case. See docs/project-workspace/04-subcontractors.md.
/// </summary>
internal static class SubcontractorsFeatures
{
    public static void AddServices(IServiceCollection services)
    {
    }

    /// <param name="api">The shared <c>/api</c> group. Routes under one project start with <c>/projects/{projectId:int}</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
    }
}
