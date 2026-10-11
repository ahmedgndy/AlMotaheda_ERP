using System.Diagnostics;

namespace AlMotaheda.Api.Infrastructure.ErrorHandling;

/// <summary>
/// Makes every error response use the same ProblemDetails JSON shape (<c>application/problem+json</c>):
/// <c>type</c>, <c>title</c>, <c>status</c>, <c>instance</c>, <c>traceId</c>, and <c>detail</c> in Development only.
/// </summary>
internal static class ErrorHandlingSetup
{
    public static IServiceCollection AddErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var httpContext = context.HttpContext;

            context.ProblemDetails.Instance ??= httpContext.Request.Path;

            // Same value as the TraceId on every log entry of this request,
            // so a user-reported error can be matched to its logs.
            context.ProblemDetails.Extensions.TryAdd(
                "traceId",
                Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier);
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services;
    }

    public static WebApplication UseErrorHandling(this WebApplication app)
    {
        // Unhandled exceptions -> GlobalExceptionHandler -> ProblemDetails.
        app.UseExceptionHandler();

        // Error status codes returned without a body (404 unknown route, 405, 415, ...) -> ProblemDetails.
        app.UseStatusCodePages();

        return app;
    }
}
