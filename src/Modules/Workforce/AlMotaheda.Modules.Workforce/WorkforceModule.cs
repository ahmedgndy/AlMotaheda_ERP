using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features;
using AlMotaheda.Modules.Workforce.Integration;
using AlMotaheda.Modules.Workforce.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Workforce;

/// <summary>
/// Entry point of the Workforce module: daily-paid workers, their assignment to projects, daily labor sheets, the allocation of labor to project items, and worker payments.
/// This is the only public type in the module.
/// </summary>
/// <remarks>
/// It uses the Projects module only through <c>AlMotaheda.Modules.Projects.Contracts</c> (which projects a caller may
/// see, project items) and the Access module only through <c>AlMotaheda.Modules.Access.Contracts</c> (user names).
/// </remarks>
public sealed class WorkforceModule : IModule
{
    public string Name => "Workforce";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(WorkforceDatabase.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{WorkforceDatabase.ConnectionStringName} is missing. Set it with user secrets in development "
                + $"or the ConnectionStrings__{WorkforceDatabase.ConnectionStringName} environment variable on the server.");
        }

        services.AddDbContext<WorkforceDbContext>(options => options
            .UseSqlServer(connectionString, WorkforceDatabase.Configure)
            // Failures are logged once, by the host's exception handler; expected ones (a duplicate key) become 409s.
            .ConfigureWarnings(warnings => warnings.Ignore(
                CoreEventId.SaveChangesFailed,
                CoreEventId.QueryIterationFailed,
                RelationalEventId.CommandError,
                RelationalEventId.ConnectionError)));

        services.AddOptions<WorkforceOptions>().Bind(configuration.GetSection(WorkforceOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<WorkforceOptions>, WorkforceOptionsValidator>();

        services.AddCompanyClock();
        services.AddSingleton<WorkforceLog>();
        services.AddScoped<ILaborItemCosts, LaborItemCosts>();
        services.AddScoped<IProjectCostSource, LaborCostSource>();
        services.AddScoped<IWorkInboxSource, WorkforceInboxSource>();
        services.AddScoped<IProjectRecordOwnership, WorkforceRecordOwnership>();
        WorkforceFeatures.AddServices(services);

        services.AddHostedService<WorkforceStartup>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        WorkforceFeatures.Map(api);
    }
}
