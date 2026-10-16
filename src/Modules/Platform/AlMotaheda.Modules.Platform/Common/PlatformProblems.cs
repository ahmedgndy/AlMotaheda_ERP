using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Platform.Common;

/// <summary>Error responses of the Platform module.</summary>
internal static class PlatformProblems
{
    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    /// <summary>The notification doesn't exist or belongs to someone else; both answer 404.</summary>
    public static ProblemHttpResult NotificationNotFound() => ApiProblem.NotFoundResult("The notification was not found.");
}
