using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Archive.Features;

/// <summary>
/// Wires the Archive module's features: one line per use case. See docs/project-workspace/06-archive.md.
/// </summary>
internal static class ArchiveFeatures
{
    public static void AddServices(IServiceCollection services)
    {
    }

    /// <param name="api">The shared <c>/api</c> group. Routes under one project start with <c>/projects/{projectId:int}</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
    }
}
