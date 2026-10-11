using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <para>
/// Register optional dependencies (a cache, for example) with <c>failureStatus: HealthStatus.Degraded</c>.
/// Degraded still returns 200, so an outage of something the app can live without doesn't take
/// servers out of the load balancer. Give every check a timeout.
/// </para>
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Leave "description" out of the JSON when it isn't shown, rather than sending null.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static Task WriteJsonResponse(HttpContext context, HealthReport report)
    {
        // This endpoint is anonymous. When a check throws, .NET puts the exception message into the
        // check's description, which can reveal host names or connection strings. So descriptions are
        // shown only in Development. Exceptions and check data are never written.
        var showDescriptions = context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();

        var response = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = showDescriptions ? entry.Value.Description : null,
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 2),
            }),
        };

        return context.Response.WriteAsJsonAsync(response, JsonOptions);
    }
}
