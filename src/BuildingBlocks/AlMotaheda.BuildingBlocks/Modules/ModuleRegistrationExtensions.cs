using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.BuildingBlocks.Modules;

/// <summary>
/// Composes business modules into the host in two steps:
/// <see cref="AddModules"/> while services are configured, then <see cref="MapModules"/> after the app is built.
/// </summary>
public static class ModuleRegistrationExtensions
{
    /// <summary>The route prefix shared by all module endpoints.</summary>
    public const string ApiRoutePrefix = "/api";

    private const string LoggerCategory = "AlMotaheda.Modules";

    /// <summary>
    /// Lets each module register its services, and remembers the module so its endpoints can be mapped later.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two modules share the same name.</exception>
    public static IServiceCollection AddModules(
        this IServiceCollection services,
        IConfiguration configuration,
        IEnumerable<IModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(modules);

        var registeredNames = services
            .Where(descriptor => descriptor.ServiceType == typeof(IModule))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<IModule>()
            .Select(module => module.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var module in modules)
        {
            if (!registeredNames.Add(module.Name))
            {
                throw new InvalidOperationException(
                    $"A module named '{module.Name}' is already registered. Module names must be unique.");
            }

            module.RegisterServices(services, configuration);
            services.AddSingleton(module);
        }

        return services;
    }

    /// <summary>
    /// Maps the endpoints of every registered module under <see cref="ApiRoutePrefix"/>.
    /// </summary>
    /// <returns>
    /// The shared API route group. Conventions added to it apply to every module endpoint
    /// (for example, a default authorization requirement once authentication exists).
    /// </returns>
    public static RouteGroupBuilder MapModules(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        var apiGroup = endpoints.MapGroup(ApiRoutePrefix);

        foreach (var module in services.GetServices<IModule>())
        {
            module.MapEndpoints(apiGroup);
            logger.LogInformation("Module {ModuleName} loaded", module.Name);
        }

        return apiGroup;
    }
}
