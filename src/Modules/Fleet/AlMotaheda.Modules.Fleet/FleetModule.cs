using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features;
using AlMotaheda.Modules.Fleet.Features.Gps;
using AlMotaheda.Modules.Fleet.Integration;
using AlMotaheda.Modules.Fleet.Startup;
using AlMotaheda.Modules.Fleet.Storage;
using Microsoft.Extensions.Options;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
        services.AddModuleDbContext<FleetDbContext>(configuration, FleetDatabase.Configure);

        services.AddCompanyClock();
        services.AddScoped<IProjectCostSource, EquipmentCostSource>();
        services.AddScoped<IEquipmentItemCosts, EquipmentItemCosts>();
        services.AddScoped<IWorkInboxSource, FleetInboxSource>();
        services.AddScoped<IPaymentSource, EquipmentCostPaymentSource>();
        services.AddScoped<IProjectRecordOwnership, FleetRecordOwnership>();
        services.AddScoped<IReportSource, FleetReports>();
        services.AddScoped<IReportSource, VehicleReports>();

        // Vehicles: policies, the evidence store, the custody ledger for Finance, Price Control, GPS (no provider yet).
        services.AddOptions<FleetOptions>().Bind(configuration.GetSection(FleetOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<FleetOptions>, FleetOptionsValidator>();
        services.AddOptions<FleetStorageOptions>().Bind(configuration.GetSection(FleetStorageOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<FleetStorageOptions>, FleetStorageOptionsSetup>();
        services.AddSingleton<IValidateOptions<FleetStorageOptions>, FleetStorageOptionsValidator>();
        services.AddSingleton<IFleetFileStore, LocalFleetFileStore>();
        services.AddScoped<ICustodyLedgerSource, VehicleCustodyLedgerSource>();
        services.AddScoped<IPriceExceptionSource, VehicleExpensePriceExceptionSource>();
        services.AddSingleton<IVehicleLocationProvider, NullVehicleLocationProvider>();
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
