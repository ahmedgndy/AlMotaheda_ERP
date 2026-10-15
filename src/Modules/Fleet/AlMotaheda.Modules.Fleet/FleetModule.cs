using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features;
using AlMotaheda.Modules.Fleet.Integration;
using AlMotaheda.Modules.Fleet.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Fleet;

/// <summary>
/// Entry point of the Fleet module: site equipment: the company register, rental terms, assignment to projects, and equipment costs.
/// This is the only public type in the module.
/// </summary>
/// <remarks>
/// It uses the Projects module only through <c>AlMotaheda.Modules.Projects.Contracts</c> (which projects a caller may
/// see, project items) and the Access module only through <c>AlMotaheda.Modules.Access.Contracts</c> (user names).
/// </remarks>
public sealed class FleetModule : IModule
{
    public string Name => "Fleet";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(FleetDatabase.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{FleetDatabase.ConnectionStringName} is missing. Set it with user secrets in development "
                + $"or the ConnectionStrings__{FleetDatabase.ConnectionStringName} environment variable on the server.");
        }

        services.AddDbContext<FleetDbContext>(options => options
            .UseSqlServer(connectionString, FleetDatabase.Configure)
            // Failures are logged once, by the host's exception handler; expected ones (a duplicate key) become 409s.
            .ConfigureWarnings(warnings => warnings.Ignore(
                CoreEventId.SaveChangesFailed,
                CoreEventId.QueryIterationFailed,
                RelationalEventId.CommandError,
                RelationalEventId.ConnectionError)));

        services.AddCompanyClock();
        services.AddScoped<IProjectCostSource, EquipmentCostSource>();
        services.AddScoped<IWorkInboxSource, FleetInboxSource>();
        services.AddScoped<IProjectRecordOwnership, FleetRecordOwnership>();
        FleetFeatures.AddServices(services);

        services.AddHostedService<FleetStartup>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        FleetFeatures.Map(api);
    }
}
