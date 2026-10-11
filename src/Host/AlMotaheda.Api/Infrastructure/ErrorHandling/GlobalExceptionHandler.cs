using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AlMotaheda.Api.Infrastructure.ErrorHandling;

/// <summary>
/// Turns any exception that escapes an endpoint into an RFC 9457 ProblemDetails response and logs it once.
/// </summary>
/// <remarks>
/// <para>
/// Expected business failures (validation errors, "not found", ...) should be returned as results by the
/// feature itself. This handler is the safety net for everything unexpected.
/// </para>
/// <para>
/// This is the only place that logs unhandled exceptions. Since .NET 10, the framework's
/// ExceptionHandlerMiddleware no longer logs an exception that a handler has handled, so each one is
/// logged once, here, and client errors are logged as warnings rather than server errors.
/// </para>
/// <para>
/// Requests the client aborted never reach this handler: the middleware answers those itself.
/// </para>
/// </remarks>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var request = httpContext.Request;

        var statusCode = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception while processing {Method} {Path}", request.Method, request.Path);
        }
        else
        {
            // A client mistake, not a server fault: log the reason without a stack trace.
            logger.LogWarning(
                "Bad request while processing {Method} {Path}: {Reason}",
                request.Method,
                request.Path,
                exception.Message);
        }

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,

            // Exception messages can reveal internals (SQL, file paths, ...), so only Development shows them.
            Detail = environment.IsDevelopment() ? exception.Message : null,
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception,
        });
    }
}
