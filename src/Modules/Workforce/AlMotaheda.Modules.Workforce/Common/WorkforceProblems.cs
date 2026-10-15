using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Workforce.Common;

/// <summary>The Workforce module's error responses. Shared codes come from <see cref="ApiProblem"/>.</summary>
internal static class WorkforceProblems
{
    public const string ProjectCompleted = "project_completed";
    public const string OwnRecord = "own_record";
    public const string InvalidStatus = "invalid_status";
    public const string Busy = "busy";
    public const string TradeCodeTaken = "trade_code_taken";
    public const string NationalIdTaken = "national_id_taken";
    public const string WorkerInactive = "worker_inactive";
    public const string NotAssigned = "not_assigned";
    public const string AlreadyAssigned = "already_assigned";
    public const string SheetExists = "sheet_exists";
    public const string WorkerAlreadyRecorded = "worker_already_recorded";
    public const string AllocationExceedsDay = "allocation_exceeds_day";
    public const string ItemInactive = "item_inactive";
    public const string NegativeNet = "negative_net";
    public const string OverPayment = "over_payment";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string PaymentAlreadyVoided = "payment_already_voided";
    public const string ItemGone = "item_removed";

    public static ProblemHttpResult TradeNotFound() => NotFound("trade");

    public static ProblemHttpResult WorkerNotFound() => NotFound("worker");

    public static ProblemHttpResult AssignmentNotFound() => NotFound("assignment");

    public static ProblemHttpResult SheetNotFound() => NotFound("labor sheet");

    public static ProblemHttpResult LineNotFound() => NotFound("labor line");

    public static ProblemHttpResult PaymentNotFound() => NotFound("payment");

    public static ProblemHttpResult CodeTaken() =>
        Conflict(TradeCodeTaken, "A trade with this code already exists.");

    public static ProblemHttpResult NationalIdUsed() =>
        Conflict(NationalIdTaken, "A worker with this national id already exists.");

    public static ProblemHttpResult InactiveWorker(int workerId) =>
        Conflict(WorkerInactive, $"Worker {workerId} is inactive and can't be recorded or assigned.");

    public static ProblemHttpResult WorkerNotOnProject(int workerId, DateOnly date) =>
        Conflict(NotAssigned, $"Worker {workerId} is not assigned to this project on {date:yyyy-MM-dd}.");

    public static ProblemHttpResult AlreadyOnProject() =>
        Conflict(AlreadyAssigned, "This worker is already assigned to this project for that date.");

    public static ProblemHttpResult SheetAlreadyExists() =>
        Conflict(SheetExists, "You already have a labor sheet for this project, day and shift. Edit that sheet instead.");

    public static ProblemHttpResult AlreadyRecorded(int? workerId) =>
        Conflict(
            WorkerAlreadyRecorded,
            workerId is null
                ? "A worker on this sheet is already recorded for this day and shift on another sheet."
                : $"Worker {workerId} is already recorded for this day and shift on another sheet.");

    public static ProblemHttpResult ExceedsDay(int? lineIndexOrId) =>
        Conflict(AllocationExceedsDay, $"The item portions of line {lineIndexOrId} add up to more than one day.");

    public static ProblemHttpResult InactiveItem(int itemId) =>
        Conflict(ItemInactive, $"Project item {itemId} is inactive. Labor can't be newly linked to it.");

    /// <summary>A project item was deleted by someone else between our check and our save.</summary>
    public static ProblemHttpResult ItemRemoved() =>
        Conflict(ItemGone, "A project item was removed while you were saving. Reload the items and try again.");

    public static ProblemHttpResult NetBelowZero(int workerId) =>
        Conflict(NegativeNet, $"The deductions of worker {workerId} are more than the day's pay. The net pay can't be below zero.");

    /// <param name="outstanding">The unpaid pay to name, or <c>null</c> when part of it is on projects the caller can't see.</param>
    public static ProblemHttpResult PaysTooMuch(decimal? outstanding) =>
        Conflict(OverPayment, outstanding is { } shown
            ? $"The payment is more than the worker's approved unpaid pay ({shown:0.00})."
            : "The payment is more than the worker's approved unpaid pay.");

    public static ProblemHttpResult KeyUsedElsewhere() =>
        Conflict(IdempotencyKeyReused, "This idempotency key was already used for a different payment. Make a new key for a new payment.");

    public static ProblemHttpResult AlreadyVoided() =>
        Conflict(PaymentAlreadyVoided, "This payment is already voided.");

    public static ProblemHttpResult NotTheRecorder() =>
        ApiProblem.ForbiddenResult("Only the engineer who recorded this sheet can change it.");

    public static ProblemHttpResult AllocationNotAllowed() =>
        ApiProblem.ForbiddenResult("Changing item allocations needs workers Edit or workers Approve, or being the recorder of a draft or returned sheet.");

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    /// <summary>
    /// The project doesn't exist, or the caller may not see it. Both get the same answer, so a guessed id
    /// reveals nothing.
    /// </summary>
    public static ProblemHttpResult ProjectNotFound() => ApiProblem.NotFoundResult("The project was not found.");

    public static ProblemHttpResult NotFound(string what) => ApiProblem.NotFoundResult($"The {what} was not found.");

    public static ProblemHttpResult CompletedProject() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ProjectCompleted, "A completed project can only be changed by an Executive.");

    /// <summary>The caller recorded this record, so someone else must review it.</summary>
    public static ProblemHttpResult ReviewerIsRecorder() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, OwnRecord, "You can't review a record you recorded. Another reviewer must do it.");

    /// <summary>The record is not in a status that allows this action.</summary>
    public static ProblemHttpResult WrongStatus(string title) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, InvalidStatus, title);

    public static ProblemHttpResult Conflict(string code, string title) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, code, title);

    /// <summary>Another change to the same records held its lock for too long; safe to retry.</summary>
    public static ProblemHttpResult TryAgainLater() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy, "Another change is still running. Try again in a moment.");
}
