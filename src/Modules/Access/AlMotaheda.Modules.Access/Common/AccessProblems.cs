using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Access.Common;

/// <summary>The Access module's error responses. Codes are listed in the design's "Errors" table.</summary>
internal static class AccessProblems
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string InvalidCurrentPassword = "invalid_current_password";
    public const string WeakPassword = "weak_password";
    public const string EmailTaken = "email_taken";
    public const string PhoneTaken = "phone_taken";
    public const string LastExecutive = "last_executive";
    public const string ExecutiveRoleLocked = "executive_role_locked";
    public const string SelfChangeNotAllowed = "self_change_not_allowed";
    public const string Busy = "busy";

    /// <summary>
    /// 400 with field errors. The code is <c>weak_password</c> when only password fields failed,
    /// otherwise <c>validation_failed</c>.
    /// </summary>
    public static ValidationProblem Validation(ValidationErrors errors, params string[] passwordFields)
    {
        var dictionary = errors.ToDictionary();
        var onlyPasswordFields = passwordFields.Length > 0 && dictionary.Keys.All(passwordFields.Contains);

        return TypedResults.ValidationProblem(
            dictionary,
            title: onlyPasswordFields ? "The password doesn't meet the password rules." : "One or more fields are invalid.",
            extensions: new Dictionary<string, object?>
            {
                [ApiProblem.CodeKey] = onlyPasswordFields ? WeakPassword : ApiProblem.ValidationFailed,
            });
    }

    /// <summary>The one answer for every sign-in failure, so the response never reveals whether an account exists.</summary>
    public static ProblemHttpResult SignInFailed() =>
        ApiProblem.Create(StatusCodes.Status401Unauthorized, InvalidCredentials, "The email or password is incorrect.");

    /// <summary>Refresh failed: the sign-in has ended (expired, signed out, revoked or replayed).</summary>
    public static ProblemHttpResult SessionEnded() =>
        ApiProblem.Create(StatusCodes.Status401Unauthorized, ApiProblem.Unauthenticated, "Your session has ended. Sign in again.");

    public static ProblemHttpResult WrongCurrentPassword() =>
        ApiProblem.Create(StatusCodes.Status400BadRequest, InvalidCurrentPassword, "The current password is incorrect.");

    public static ProblemHttpResult ExecutiveOnly() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, ApiProblem.Forbidden, "Only an Executive can do this.");

    public static ProblemHttpResult SelfChange() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, SelfChangeNotAllowed, "You can't change your own status, role or permissions.");

    public static ProblemHttpResult EmailAlreadyUsed() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, EmailTaken, "Another user already has this email.");

    public static ProblemHttpResult PhoneAlreadyUsed() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PhoneTaken, "Another user already has this phone number.");

    public static ProblemHttpResult WouldRemoveLastExecutive() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, LastExecutive, "At least one active Executive must remain.");

    /// <summary>Another user-management change held the lock for too long; safe to retry.</summary>
    public static ProblemHttpResult TryAgainLater() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy, "Another change to users is still running. Try again in a moment.");

    public static ProblemHttpResult ExecutiveLocked() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            ExecutiveRoleLocked,
            "The Executive role's permissions are fixed, and Executives can't have their own permissions.");
}
