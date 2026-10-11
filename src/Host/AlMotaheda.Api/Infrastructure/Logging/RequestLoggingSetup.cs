using Microsoft.AspNetCore.HttpLogging;

namespace AlMotaheda.Api.Infrastructure.Logging;

/// <summary>
/// Logs every HTTP request as one structured entry (method, path, status code, duration).
/// </summary>
/// <remarks>
/// Logging uses the built-in .NET providers, configured in the "Logging" section of appsettings:
/// readable single-line text locally, one JSON object per line in Production.
/// Trace and span ids are attached to every entry automatically, so all logs of one request can be correlated
/// with the <c>traceId</c> returned in error responses.
/// </remarks>
internal static class RequestLoggingSetup
{
    public static IServiceCollection AddRequestLogging(this IServiceCollection services)
    {
        services.AddHttpLogging(options =>
        {
            // Never log headers, query strings, or bodies.
            // They can hold access tokens, personal data, or uploaded file content.
            options.LoggingFields =
                HttpLoggingFields.RequestMethod
                | HttpLoggingFields.RequestPath
                | HttpLoggingFields.ResponseStatusCode
                | HttpLoggingFields.Duration;

            // One log entry per request instead of separate request and response entries.
            options.CombineLogs = true;
        });

        return services;
    }

    public static WebApplication UseRequestLogging(this WebApplication app)
    {
        app.UseHttpLogging();
        return app;
    }
}
