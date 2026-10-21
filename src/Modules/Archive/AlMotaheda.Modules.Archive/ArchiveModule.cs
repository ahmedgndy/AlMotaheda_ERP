using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Features;
using AlMotaheda.Modules.Archive.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Archive;

/// <summary>
/// Entry point of the Archive module: the documents and media of each project: upload, storage, search, preview and download.
/// This is the only public type in the module.
/// </summary>
/// <remarks>
/// <para>It uses the Projects module only through <c>AlMotaheda.Modules.Projects.Contracts</c> (which projects a caller
/// may see) and the Access module only through <c>AlMotaheda.Modules.Access.Contracts</c> (uploader names).</para>
/// <para>It registers its own <c>archive-storage</c> health check and checks its storage folder at startup, so the
/// host needs no Archive-specific code.</para>
/// </remarks>
public sealed class ArchiveModule : IModule
{
    public string Name => "Archive";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ArchiveDbContext>(configuration, ArchiveDatabase.Configure);

        services.AddCompanyClock();
        services.AddScoped<Projects.Contracts.IRecordDocuments, Integration.RecordDocuments>();
        ArchiveFeatures.AddServices(services, configuration);

        services.AddHostedService<ArchiveStartup>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        ArchiveFeatures.Map(api);
    }
}
