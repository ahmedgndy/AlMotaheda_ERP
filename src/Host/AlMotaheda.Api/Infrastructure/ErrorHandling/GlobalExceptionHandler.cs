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
/// This is the only place that logs unhandled exceptions. The framework's own ExceptionHandlerMiddleware
/// log is switched off in appsettings.json ("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware": "None");
/// otherwise every exception would be logged twice, and client errors would be logged as server errors.
/// </para>
/// </remarks>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    // Non-standard status code (used by nginx and others) meaning "client closed the request".
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var request = httpContext.Request;

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away. Nobody is waiting for an answer and it is not a server fault.
            logger.LogInformation("Request {Method} {Path} was cancelled by the client", request.Method, request.Path);
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

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
