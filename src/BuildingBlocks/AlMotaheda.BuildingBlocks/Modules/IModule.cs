using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.BuildingBlocks.Modules;

/// <summary>
/// The contract every business module implements so the API host can plug it in.
/// </summary>
/// <remarks>
/// A module owns one business area end to end: its endpoints, services, and (later) its data.
/// The host only knows this interface. It never touches the module's internal types.
/// </remarks>
public interface IModule
{
    /// <summary>
    /// Unique, human-readable module name. Used in logs and to detect duplicate registrations.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Registers the services this module needs (feature services, options, data access, ...).
    /// Called once at startup, before the application is built.
    /// </summary>
    void RegisterServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Maps this module's HTTP endpoints. Called once at startup, after the application is built.
    /// </summary>
    /// <param name="endpoints">
    /// The shared API route group (prefix <c>/api</c>). The module adds its own sub-groups,
    /// for example <c>endpoints.MapGroup("/projects")</c>.
    /// </param>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
