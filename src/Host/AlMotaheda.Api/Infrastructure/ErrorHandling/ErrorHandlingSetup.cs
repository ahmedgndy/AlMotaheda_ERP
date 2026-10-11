using System.Diagnostics;
using AlMotaheda.BuildingBlocks.Http;

namespace AlMotaheda.Api.Infrastructure.ErrorHandling;

/// <summary>
/// Makes every error response use the same ProblemDetails JSON shape (<c>application/problem+json</c>):
/// <c>type</c>, <c>title</c>, <c>status</c>, <c>instance</c>, <c>traceId</c>, <c>code</c>, and <c>detail</c> in Development only.
/// </summary>
internal static class ErrorHandlingSetup
{
    public static IServiceCollection AddErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var httpContext = context.HttpContext;

            context.ProblemDetails.Instance ??= httpContext.Request.Path;

            // Same value as the TraceId on every log entry of this request, so a user-reported error
            // can be matched to its logs. Always set (never just added), so the value keeps this format
            // whatever the framework adds by default.
            context.ProblemDetails.Extensions["traceId"] =
                Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

            // Every error carries a machine-readable "code". Features set specific ones; errors the framework
            // produces itself (unknown route, malformed JSON, ...) get one from their status.
            context.ProblemDetails.Extensions.TryAdd(ApiProblem.CodeKey, DefaultCode(context.ProblemDetails.Status));
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services;
    }

    internal static string DefaultCode(int? status) => status switch
    {
        StatusCodes.Status400BadRequest => ApiProblem.ValidationFailed,
        StatusCodes.Status401Unauthorized => ApiProblem.Unauthenticated,
        StatusCodes.Status403Forbidden => ApiProblem.Forbidden,
        StatusCodes.Status404NotFound => ApiProblem.NotFound,
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status429TooManyRequests => ApiProblem.TooManyRequests,
        >= 500 => "server_error",
        _ => "error",
    };

    public static WebApplication UseErrorHandling(this WebApplication app)
    {
        // Unhandled exceptions -> GlobalExceptionHandler -> ProblemDetails.
        app.UseExceptionHandler();

        // Error status codes returned without a body (404 unknown route, 405, 415, ...) -> ProblemDetails.
        app.UseStatusCodePages();

        return app;
    }
}
