using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Finance.Common;

/// <summary>Error responses of the Finance module, each with its machine <c>code</c>.</summary>
internal static class FinanceProblems
{
    public const string InvalidStatus = "invalid_status";
    public const string WrongMethod = "wrong_method";
    public const string OwnRecord = "own_record";
    public const string NotPayable = "not_payable";
    public const string BankRefused = "bank_refused";
    public const string Busy = "busy";
    public const string PermissionRevoked = "permission_revoked";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    public static ProblemHttpResult RequestNotFound() => ApiProblem.NotFoundResult("The payment request was not found.");

    public static ProblemHttpResult WrongStatus(string status) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, InvalidStatus, $"This can't be done to a request that is {status}.");

    public static ProblemHttpResult WrongPaymentMethod(string method) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, WrongMethod, $"This step doesn't apply to a {method} payment.");

    public static ProblemHttpResult OwnRequest() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, OwnRecord, "You created this request, so someone else must decide it.");

    public static ProblemHttpResult NotPayableAnymore(string reason) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, NotPayable, reason);

    public static ProblemHttpResult BankRefusedOrder(string? message) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, BankRefused, message ?? "The bank refused the payment order.");

    public static ProblemHttpResult TryAgainLater() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy, "Someone else is working on this request. Try again in a moment.");

    /// <summary>The token still says yes, but the database says the permission was removed or the sign-in ended.</summary>
    public static ProblemHttpResult Revoked() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, PermissionRevoked, "Your permission for this changed. Sign in again.");
}
