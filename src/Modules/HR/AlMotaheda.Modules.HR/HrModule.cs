using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Features;
using AlMotaheda.Modules.HR.Integration;
using AlMotaheda.Modules.HR.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.HR;

/// <summary>
/// Entry point of the HR module: departments, employees, their documents, salary structures, payroll inputs and the
/// monthly salary sheet (docs/modernization/10-hr-design.md). This is the only public type in the module.
/// </summary>
/// <remarks>
/// It uses the Access module only through <c>Access.Contracts</c> (user names) and Finance only through
/// <c>Finance.Contracts</c> (salary payment requests, and settling them).
/// </remarks>
public sealed class HrModule : IModule
{
    public string Name => "HR";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<HrDbContext>(configuration, HrDatabase.Configure);

        services.AddCompanyClock();
        services.AddScoped<IPaymentSource, SalarySheetPaymentSource>();
        HrFeatures.AddServices(services);

        services.AddHostedService<HrStartup>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        HrFeatures.Map(api);
    }
}
