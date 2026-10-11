using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>The Projects module's error responses. Shared codes come from <see cref="ApiProblem"/>.</summary>
internal static class ProjectProblems
{
    public const string ProjectCodeTaken = "project_code_taken";
    public const string ProjectCompleted = "project_completed";
    public const string UserNotAssignable = "user_not_assignable";
    public const string AlreadyMember = "already_member";
    public const string ManagerRequired = "manager_required";
    public const string MembershipEnded = "membership_already_ended";
    public const string Busy = "busy";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    /// <summary>
    /// The project doesn't exist, or the caller may not see it. Both get the same answer, so a guessed id
    /// reveals nothing.
    /// </summary>
    public static ProblemHttpResult ProjectNotFound() => ApiProblem.NotFoundResult("The project was not found.");

    public static ProblemHttpResult MembershipNotFound() => ApiProblem.NotFoundResult("The membership was not found.");

    public static ProblemHttpResult ExecutiveOnly() =>
        ApiProblem.ForbiddenResult("Only an Executive can do this.");

    public static ProblemHttpResult EditNotAllowed() =>
        ApiProblem.ForbiddenResult("Only an Executive or this project's manager can edit it.");

    public static ProblemHttpResult CodeChangeNotAllowed() =>
        ApiProblem.ForbiddenResult("Only an Executive can change the project code.");

    public static ProblemHttpResult CompletedProject() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ProjectCompleted, "A completed project can only be changed by an Executive.");

    public static ProblemHttpResult CodeTaken() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ProjectCodeTaken, "A project with this code already exists.");

    public static ProblemHttpResult AlreadyOnProject() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, AlreadyMember, "This person is already on the project.");

    public static ProblemHttpResult ManagerCannotBeEnded() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            ManagerRequired,
            "A project always has a manager. Assign a new project manager instead.");

    public static ProblemHttpResult AlreadyEnded() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, MembershipEnded, "This membership has already ended.");

    /// <summary>Another change to the same project held its lock for too long; safe to retry.</summary>
    public static ProblemHttpResult TryAgainLater() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy, "Another change to this project is still running. Try again in a moment.");

    /// <summary>The user is unknown, disabled, or their role doesn't fit the responsibility.</summary>
    public static ProblemHttpResult NotAssignable(string title) =>
        ApiProblem.Create(StatusCodes.Status400BadRequest, UserNotAssignable, title);
}
