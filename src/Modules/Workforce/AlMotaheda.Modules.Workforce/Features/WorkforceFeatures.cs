using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Workforce.Features;

/// <summary>
/// Wires the Workforce module's features: one line per use case. See docs/project-workspace/03-workforce.md.
/// </summary>
internal static class WorkforceFeatures
{
    public static void AddServices(IServiceCollection services)
    {
    }

    /// <param name="api">The shared <c>/api</c> group. Routes under one project start with <c>/projects/{projectId:int}</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
    }
}
