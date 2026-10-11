using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.BuildingBlocks.Http;

/// <summary>
/// Error responses with a machine-readable <c>code</c>, so the frontend can show its own Arabic or English message.
/// </summary>
/// <remarks>
/// All of them use the host's ProblemDetails format, which also adds <c>instance</c> and <c>traceId</c>.
/// Module-specific codes are defined by the module; the codes below are shared by every module.
/// </remarks>
public static class ApiProblem
{
    /// <summary>The ProblemDetails extension that carries the error code.</summary>
    public const string CodeKey = "code";

    public const string ValidationFailed = "validation_failed";
    public const string Unauthenticated = "unauthenticated";
    public const string Forbidden = "forbidden";
    public const string PasswordChangeRequired = "password_change_required";
    public const string NotFound = "not_found";
    public const string ConcurrencyConflict = "concurrency_conflict";
    public const string TooManyRequests = "too_many_requests";

    public static ProblemHttpResult Create(int status, string code, string title) =>
        TypedResults.Problem(
            statusCode: status,
            title: title,
            extensions: new Dictionary<string, object?> { [CodeKey] = code });

    /// <summary>400 with the failing fields listed under <c>errors</c>.</summary>
    public static ValidationProblem Validation(IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            title: "One or more fields are invalid.",
            extensions: new Dictionary<string, object?> { [CodeKey] = ValidationFailed });

    public static ProblemHttpResult NotFoundResult(string title = "The record was not found.") =>
        Create(StatusCodes.Status404NotFound, NotFound, title);

    public static ProblemHttpResult ForbiddenResult(string title = "You don't have permission to do this.") =>
        Create(StatusCodes.Status403Forbidden, Forbidden, title);

    public static ProblemHttpResult ConcurrencyConflictResult() =>
        Create(
            StatusCodes.Status409Conflict,
            ConcurrencyConflict,
            "Someone else saved this record first. Reload it and try again.");
}
