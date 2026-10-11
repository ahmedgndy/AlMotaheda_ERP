using AlMotaheda.BuildingBlocks.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Archive;

/// <summary>
/// Entry point of the Archive module. This is the only public type in the module.
/// </summary>
public sealed class ArchiveModule : IModule
{
    public string Name => "Archive";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Feature services are registered here as vertical slices are added.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Feature endpoints are mapped here as vertical slices are added. Example:
        //   var documents = endpoints.MapGroup("/projects/{projectId:guid}/documents").WithTags(Name);
        //   UploadDocument.MapEndpoint(documents);
    }
}
