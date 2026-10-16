using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.Api.Infrastructure.ApiDocumentation;
using AlMotaheda.Api.Infrastructure.ErrorHandling;
using AlMotaheda.Api.Infrastructure.HealthChecks;
using AlMotaheda.Api.Infrastructure.Logging;
using AlMotaheda.Api.Infrastructure.RateLimiting;
using AlMotaheda.Api.Infrastructure.Security;
using AlMotaheda.BuildingBlocks.Modules;

namespace AlMotaheda.Api;

/// <summary>
/// The composition root: the one place where infrastructure and business modules are wired together.
/// </summary>
/// <remarks>
/// Program.cs and the integration tests both call these two methods,
/// so the tests always run the same pipeline as production.
/// </remarks>
internal static class ApiHost
{
    public static WebApplicationBuilder AddApiHost(this WebApplicationBuilder builder)
    {
        // Don't advertise the web server technology (removes the "Server: Kestrel" response header).
        builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

        builder.Services.AddRequestLogging();
        builder.Services.AddErrorHandling();
        builder.Services.AddApiDocumentation();
        builder.Services.AddSecurity(builder.Configuration);
        builder.Services.AddRateLimiting(builder.Configuration);
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name, timeout: DatabaseHealthCheck.Timeout);

        // One cache for every module (HybridCache); entries are removed by the dependency map when their data changes.
        builder.Services.AddErpCache();
        builder.Services.AddModules(builder.Configuration, ModuleCatalog.CreateAll());

        return builder;
    }

    public static WebApplication UseApiHost(this WebApplication app)
    {
        // Order matters:
        // 1. The real client address is restored first, so everything after it sees it.
        // 2. Request logging is outermost, so it records the final status code of every request.
        // 3. Error handling wraps all endpoints, so every failure becomes a ProblemDetails response.
        // 4. Authentication, then rate limiting, then authorization, just before the endpoints.
        app.UseClientAddress();
        app.UseRequestLogging();
        app.UseErrorHandling();

        if (app.IsApiDocumentationEnabled())
        {
            app.UseApiDocumentation();
        }

        // The live-update hub (/api/hubs/erp) prefers WebSockets; without them SignalR falls back to slower transports.
        app.UseWebSockets();

        app.UseSecurity();

        app.MapHealthCheckEndpoints();

        // Every module endpoint requires a signed-in user unless it explicitly allows anonymous access.
        app.MapModules().RequireAuthorization();

        return app;
    }
}
