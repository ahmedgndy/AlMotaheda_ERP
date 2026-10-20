using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Procurement.Features;
using AlMotaheda.Modules.Procurement.Integration;
using AlMotaheda.Modules.Procurement.Startup;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Procurement;

/// <summary>
/// Entry point of the Procurement module: requisitions, quotations, purchase orders, goods receipts, supplier
/// invoices and the 3-way match (docs/modernization/12-procurement-design.md). This is the only public type in the module.
/// </summary>
/// <remarks>
/// It reaches Projects only through <c>Projects.Contracts</c> (projects, catalog, cost items, suppliers) and
/// <c>Procurement.Contracts</c> (posting invoices as supplier cost), and Access only through <c>Access.Contracts</c>.
/// </remarks>
public sealed class ProcurementModule : IModule
{
    public string Name => "Procurement";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ProcurementDbContext>(configuration, ProcurementDatabase.Configure);

        services.AddCompanyClock();
        services.AddScoped<IProjectCostSource, CommittedCostSource>();
        services.AddScoped<Contracts.ISupplierOrders, Integration.SupplierOrders>();
        services.AddScoped<Integration.InvoicePriceChecks>();
        services.AddScoped<IPriceExceptionSource, Integration.InvoicePriceExceptionSource>();
        ProcurementFeatures.AddServices(services);

        services.AddHostedService<ProcurementStartup>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        ProcurementFeatures.Map(api);
    }
}
