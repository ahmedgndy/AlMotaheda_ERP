using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Reports.Common;
using AlMotaheda.Modules.Reports.Data;
using AlMotaheda.Modules.Reports.Engine;
using AlMotaheda.Modules.Reports.Features;
using AlMotaheda.Modules.Reports.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Reports;

/// <summary>
/// Entry point of the Reports module: the report catalog, running, Excel export, print pages and background exports
/// (docs/modernization/14-reports-design.md). The rows come from the owning modules through
/// <c>AlMotaheda.Modules.Reports.Contracts.IReportSource</c>. This is the only public type in the module.
/// </summary>
public sealed class ReportsModule : IModule
{
    public string Name => "Reports";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ReportsDbContext>(configuration, ReportsDatabase.Configure);
        services.AddOptions<ReportsOptions>().Bind(configuration.GetSection(ReportsOptions.SectionName));

        services.AddCompanyClock();
        services.AddSingleton<ReportsLog>();
        services.AddScoped<ReportCatalog>();
        services.AddScoped<ReportRunner>();

        services.AddHostedService<ReportsStartup>();
        services.AddHostedService<ExportWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        ReportEndpoints.Map(api);
    }
}
