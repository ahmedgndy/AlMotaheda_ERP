using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AlMotaheda.Api.Infrastructure.HealthChecks;

/// <summary>
/// Health endpoints for monitoring, load balancers, and container orchestrators.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>GET /health/live</c>: the process is up and serving HTTP. Runs no checks.</item>
///   <item><c>GET /health</c>: runs every registered health check. Returns 503 if any check is unhealthy.
///   Dependency checks (database, file storage, ...) are added here as those parts are built.</item>
/// </list>
/// </remarks>
internal static class HealthCheckEndpoints
{
    public const string LivenessPath = "/health/live";
    public const string ReadinessPath = "/health";

    public static IEndpointRouteBuilder MapHealthCheckEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivenessPath, new HealthCheckOptions
            {
                Predicate = _ => false,
                ResponseWriter = WriteJsonResponse,
            })
            .WithProbeConventions();

        endpoints.MapHealthChecks(ReadinessPath, new HealthCheckOptions
            {
                ResponseWriter = WriteJsonResponse,
            })
            .WithProbeConventions();

        return endpoints;
    }

    private static IEndpointConventionBuilder WithProbeConventions(this IEndpointConventionBuilder endpoint) =>
        endpoint
            // Monitoring probes never authenticate, even after authorization is added.
            .AllowAnonymous()
            // Probes call every few seconds. Logging each call would bury real traffic.
            // Failing checks are still logged as errors by the health check service.
            .WithHttpLogging(HttpLoggingFields.None);

    private static Task WriteJsonResponse(HttpContext context, HealthReport report)
    {
        // Exceptions and check data are deliberately left out: this endpoint is unauthenticated.
        var response = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 2),
            }),
        };

        return context.Response.WriteAsJsonAsync(response);
    }
}
