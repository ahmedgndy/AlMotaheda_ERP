using AlMotaheda.BuildingBlocks.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Projects;

/// <summary>
/// Entry point of the Projects module. This is the only public type in the module.
/// </summary>
public sealed class ProjectsModule : IModule
{
    public string Name => "Projects";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Feature services are registered here as vertical slices are added.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Feature endpoints are mapped here as vertical slices are added. Example:
        //   var projects = endpoints.MapGroup("/projects").WithTags(Name);
        //   CreateProject.MapEndpoint(projects);
    }
}
