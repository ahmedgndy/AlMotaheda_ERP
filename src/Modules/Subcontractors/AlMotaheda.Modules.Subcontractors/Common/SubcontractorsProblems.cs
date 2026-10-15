using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Subcontractors.Common;

/// <summary>The Subcontractors module's error responses. Shared codes come from <see cref="ApiProblem"/>.</summary>
internal static class SubcontractorsProblems
{
    public const string ProjectCompleted = "project_completed";
    public const string OwnRecord = "own_record";
    public const string InvalidStatus = "invalid_status";
    public const string Busy = "busy";

    public const string SubcontractorExists = "subcontractor_exists";
    public const string SubcontractorInactive = "subcontractor_inactive";
    public const string AlreadyContracted = "already_contracted";
    public const string ContractNoTaken = "contract_no_taken";
    public const string ContractCompleted = "contract_completed";
    public const string WrongContractModel = "wrong_contract_model";
    public const string ExceedsContractQuantity = "exceeds_contract_quantity";
    public const string ExceedsContractCeiling = "exceeds_contract_ceiling";
    public const string ExceedsContractValue = "exceeds_contract_value";
    public const string ItemHasMeasurements = "item_has_measurements";
    public const string ItemInactive = "item_inactive";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string PaymentAlreadyVoided = "payment_already_voided";
    public const string ItemGone = "item_removed";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    /// <summary>
    /// The project doesn't exist, or the caller may not see it. Both get the same answer, so a guessed id
    /// reveals nothing.
    /// </summary>
    public static ProblemHttpResult ProjectNotFound() => ApiProblem.NotFoundResult("The project was not found.");

    public static ProblemHttpResult NotFound(string what) => ApiProblem.NotFoundResult($"The {what} was not found.");

    public static ProblemHttpResult SubcontractorNotFound() => NotFound("subcontractor");

    /// <summary>
    /// The contract doesn't exist or belongs to another project. Both get the same answer, so an id of another
    /// project's record reveals nothing.
    /// </summary>
    public static ProblemHttpResult ContractNotFound() => NotFound("contract");

    public static ProblemHttpResult ContractItemNotFound() => NotFound("contract item");

    public static ProblemHttpResult MeasurementNotFound() => NotFound("measurement");

    public static ProblemHttpResult ManpowerSheetNotFound() => NotFound("manpower sheet");

    public static ProblemHttpResult PaymentNotFound() => NotFound("payment");

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

    public static ProblemHttpResult NameTaken() =>
        Conflict(SubcontractorExists, "A subcontractor with this English name already exists.");

    public static ProblemHttpResult InactiveSubcontractor() =>
        Conflict(SubcontractorInactive, "This subcontractor is inactive. Reactivate it before giving it a new contract.");

    public static ProblemHttpResult CompanyAlreadyOnProject() =>
        Conflict(AlreadyContracted, "This subcontractor already has a contract on this project.");

    public static ProblemHttpResult ContractNoAlreadyUsed() =>
        Conflict(ContractNoTaken, "This contract number is already used by another contract.");

    public static ProblemHttpResult CompletedContract() =>
        Conflict(ContractCompleted, "This contract is completed. Its work and items can't change any more.");

    public static ProblemHttpResult ModelDoesNotFit(string title) => Conflict(WrongContractModel, title);

    public static ProblemHttpResult OverQuantity(decimal remaining) =>
        Conflict(ExceedsContractQuantity, $"This is more than the quantity left on the contract item ({remaining:0.###}).");

    public static ProblemHttpResult OverCeiling(decimal remaining) =>
        Conflict(ExceedsContractCeiling, $"This is more than what is left under the contract ceiling ({remaining:0.00}).");

    public static ProblemHttpResult OverValue(decimal remaining) =>
        Conflict(ExceedsContractValue, $"This payment is more than what is left to pay on the contract value ({remaining:0.00}).");

    public static ProblemHttpResult ItemStillHasMeasurements() =>
        Conflict(ItemHasMeasurements, "Work was measured on this contract item, so it can't be deleted.");

    public static ProblemHttpResult InactiveItem() =>
        Conflict(ItemInactive, "This project item is inactive. Work can't be linked to it.");

    /// <summary>The project item was deleted by someone else between our check and our save.</summary>
    public static ProblemHttpResult ItemRemoved() =>
        Conflict(ItemGone, "The project item was removed while you were saving. Reload the items and try again.");

    public static ProblemHttpResult KeyUsedElsewhere() =>
        Conflict(IdempotencyKeyReused, "This idempotency key was already used for a different payment. Make a new key for a new payment.");

    public static ProblemHttpResult AlreadyVoided() =>
        Conflict(PaymentAlreadyVoided, "This payment is already voided.");
}
