using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features;
using AlMotaheda.Modules.Subcontractors.Integration;
using AlMotaheda.Modules.Subcontractors.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Subcontractors;

/// <summary>
/// Entry point of the Subcontractors module: subcontractor companies, their contract on each project, contract items, measured work, manpower sheets and payments.
/// This is the only public type in the module.
/// </summary>
/// <remarks>
/// It uses the Projects module only through <c>AlMotaheda.Modules.Projects.Contracts</c> (which projects a caller may
/// see, project items) and the Access module only through <c>AlMotaheda.Modules.Access.Contracts</c> (user names).
/// </remarks>
public sealed class SubcontractorsModule : IModule
{
    public string Name => "Subcontractors";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<SubcontractorsDbContext>(configuration, SubcontractorsDatabase.Configure);

        services.AddCompanyClock();
        services.AddScoped<ISubcontractItemCosts, SubcontractItemCosts>();
        services.AddScoped<IProjectCostSource, SubcontractCostSource>();
        services.AddScoped<IWorkInboxSource, SubcontractorsInboxSource>();
        services.AddScoped<IPaymentSource, SubcontractPaymentSource>();
        services.AddScoped<IProjectRecordOwnership, SubcontractRecordOwnership>();
        SubcontractorsFeatures.AddServices(services);

        services.AddHostedService<SubcontractorsStartup>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        SubcontractorsFeatures.Map(api);
    }
}
