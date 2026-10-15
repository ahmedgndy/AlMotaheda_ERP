using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Error responses shared by the project workspace features (items, expenses, budgets, reports).</summary>
internal static class WorkspaceProblems
{
    public const string OwnRecord = "own_record";
    public const string InvalidStatus = "invalid_status";
    public const string ItemInactive = "item_inactive";
    public const string IdempotencyKeyReused = "idempotency_key_reused";

    public static ProblemHttpResult NotFound(string what) => ApiProblem.NotFoundResult($"The {what} was not found.");

    /// <summary>The caller recorded this record, so someone else must review it.</summary>
    public static ProblemHttpResult ReviewerIsRecorder() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, OwnRecord, "You can't review a record you recorded. Another reviewer must do it.");

    /// <summary>The record is not in a status that allows this action.</summary>
    public static ProblemHttpResult WrongStatus(string title) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, InvalidStatus, title);

    public static ProblemHttpResult InactiveItem() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ItemInactive, "This project item is inactive. New records can't be linked to it.");

    public static ProblemHttpResult Conflict(string code, string title) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, code, title);
}
