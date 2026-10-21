using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Features;
using AlMotaheda.Modules.Office.Integration;
using AlMotaheda.Modules.Office.Startup;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Office;

/// <summary>
/// Entry point of the Office module: the company office's petty cash custodies ("عهدة المكتب"), their append-only
/// ledger, office expenses with camera receipts, custody funding through Finance, repair and purchase requests, and
/// cash counts. This is the only public type in the module.
/// </summary>
/// <remarks>
/// It uses Access only through <c>Access.Contracts</c> (names and reviewers), Finance only through
/// <c>Finance.Contracts</c> (funding payment requests, and settling them) and Projects only through
/// <c>Projects.Contracts</c> (the central Price Control catalog and its cases).
/// </remarks>
public sealed class OfficeModule : IModule
{
    public string Name => "Office";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<OfficeDbContext>(configuration, OfficeDatabase.Configure);

        services.AddCompanyClock();
        services.AddScoped<IPaymentSource, OfficeFundingSource>();
        services.AddScoped<ICustodyLedgerSource, OfficeCustodyLedgerSource>();
        services.AddScoped<IPriceExceptionSource, OfficePriceExceptionSource>();
        OfficeFeatures.AddServices(services, configuration);

        services.AddHostedService<OfficeStartup>();

        // After the startup (migrations): opens each new month and asks Finance for the standing allocations.
        services.AddHostedService<Features.Periods.OfficePeriodWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        OfficeFeatures.Map(api);
    }
}
