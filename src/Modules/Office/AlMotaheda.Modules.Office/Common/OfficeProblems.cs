using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Office.Common;

/// <summary>The Office module's error responses. Shared codes come from <see cref="ApiProblem"/>.</summary>
internal static class OfficeProblems
{
    public const string InvalidStatus = "invalid_status";
    public const string OwnRecord = "own_record";
    public const string NotHolder = "not_custodian";
    public const string CustodyInactive = "custody_inactive";
    public const string CustodyNotEmpty = "custody_not_empty";
    public const string HolderInvalid = "holder_invalid";
    public const string ReceiptRequired = "receipt_required";
    public const string EvidenceRequired = "evidence_required";
    public const string AttachmentInvalid = "attachment_invalid";
    public const string FileTypeNotAllowed = "file_type_not_allowed";
    public const string FileTooLargeCode = "file_too_large";
    public const string FileEmptyCode = "file_empty";
    public const string StorageUnavailable = "storage_unavailable";
    public const string PriceBlocked = "price_blocked";
    public const string AlreadyApproved = "already_approved";
    public const string ExecutiveRequired = "executive_required";
    public const string InsufficientBalance = "insufficient_balance";
    public const string PriceDecisionPending = "price_decision_pending";
    public const string SpendRequestInvalid = "spend_request_invalid";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string Busy = "busy";
    public const string FinanceSeparation = "finance_separation";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    public static ProblemHttpResult NotFound(string what) => ApiProblem.NotFoundResult($"The {what} was not found.");

    public static ProblemHttpResult CustodyNotFound() => NotFound("custody");
    public static ProblemHttpResult ExpenseNotFound() => NotFound("office expense");
    public static ProblemHttpResult FundingNotFound() => NotFound("funding request");
    public static ProblemHttpResult SpendNotFound() => NotFound("repair or purchase request");
    public static ProblemHttpResult CountNotFound() => NotFound("cash count");
    public static ProblemHttpResult AttachmentNotFound() => NotFound("attachment");
    public static ProblemHttpResult AlertNotFound() => NotFound("alert");

    public static ProblemHttpResult WrongStatus(string title) => Conflict(InvalidStatus, title);

    public static ProblemHttpResult ReviewerIsSubmitter() =>
        Conflict(OwnRecord, "You recorded this or you hold this custody, so someone else must review it.");

    public static ProblemHttpResult NotTheHolder() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, NotHolder, "Only the custodian of this custody can do this.");

    public static ProblemHttpResult Inactive(string status) =>
        Conflict(CustodyInactive, $"This custody is {status}. Nothing new can be recorded on it.");

    public static ProblemHttpResult NotEmpty(decimal balance, int open) =>
        Conflict(CustodyNotEmpty, $"A custody closes only with a zero balance and nothing waiting. Balance {balance:0.00}, waiting records {open}.");

    public static ProblemHttpResult HolderNotValid() =>
        ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, HolderInvalid, "The custodian must be an active user who can record office expenses.");

    public static ProblemHttpResult NoReceipt() =>
        ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, ReceiptRequired, "Take a photo of the receipt or invoice before saving the expense.");

    public static ProblemHttpResult NoEvidence(string title) =>
        ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, EvidenceRequired, title);

    public static ProblemHttpResult BadAttachments(string title) =>
        ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, AttachmentInvalid, title);

    public static ProblemHttpResult TypeNotAllowed(string allowed) =>
        ApiProblem.Create(StatusCodes.Status415UnsupportedMediaType, FileTypeNotAllowed, $"This file type is not accepted here. Allowed: {allowed}.");

    public static ProblemHttpResult TooLarge(long maxBytes) =>
        ApiProblem.Create(StatusCodes.Status413PayloadTooLarge, FileTooLargeCode, $"The file is larger than {maxBytes / (1024 * 1024)} MB.");

    public static ProblemHttpResult EmptyFile() =>
        ApiProblem.Create(StatusCodes.Status400BadRequest, FileEmptyCode, "The file is empty. Take the photo again.");

    public static ProblemHttpResult StorageDown() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, StorageUnavailable, "Files can't be saved right now. Try again later.");

    public static ProblemHttpResult PriceRefused(decimal unitPrice, decimal maxPrice) =>
        Conflict(PriceBlocked, $"The unit price {unitPrice:0.00} is above the highest allowed price {maxPrice:0.00}, and the company policy refuses it.");

    public static ProblemHttpResult ApprovedAlready() =>
        Conflict(AlreadyApproved, "You already approved this round. A second approver is needed.");

    public static ProblemHttpResult NeedsExecutive() =>
        Conflict(ExecutiveRequired, "This expense needs two approvals and one of them must be the Executive's.");

    public static ProblemHttpResult NotEnoughCash(decimal balance, decimal amount) =>
        Conflict(InsufficientBalance, $"The custody's recorded balance ({balance:0.00}) is less than {amount:0.00}. Fund the custody first.");

    public static ProblemHttpResult WaitingForPrice(int exceptionId) =>
        Conflict(PriceDecisionPending, $"The price is waiting for the Executive's decision in Price Control (case {exceptionId}).");

    public static ProblemHttpResult BadSpendRequest(string title) =>
        Conflict(SpendRequestInvalid, title);

    public static ProblemHttpResult FinanceSeparationRequired() =>
        Conflict(FinanceSeparation, "The Accountant agrees custody funding; the Executive then approves its payment in Finance.");

    public static ProblemHttpResult KeyUsedElsewhere() =>
        Conflict(IdempotencyKeyReused, "This form was already sent with different details. Reload and try again.");

    public static ProblemHttpResult TryAgainLater() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy, "This custody is being changed by someone else. Try again in a moment.");

    private static ProblemHttpResult Conflict(string code, string title) => ApiProblem.Create(StatusCodes.Status409Conflict, code, title);
}
