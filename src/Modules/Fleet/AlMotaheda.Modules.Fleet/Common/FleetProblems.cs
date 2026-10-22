using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Fleet.Common;

/// <summary>The Fleet module's error responses. Shared codes come from <see cref="ApiProblem"/>.</summary>
internal static class FleetProblems
{
    public const string ProjectCompleted = "project_completed";
    public const string OwnRecord = "own_record";
    public const string InvalidStatus = "invalid_status";
    public const string Busy = "busy";
    public const string AssetNoTaken = "asset_no_taken";
    public const string EquipmentAssigned = "equipment_assigned";
    public const string EquipmentUnavailable = "equipment_unavailable";
    public const string NeverOnProject = "never_on_project";
    public const string AlreadyPaid = "already_paid";
    public const string PaymentRequested = "payment_requested";
    public const string AdjustmentOpen = "adjustment_open";
    public const string AdjustmentBelowPaid = "adjustment_below_paid";
    public const string CostBeingPaid = "cost_being_paid";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string NotPaid = "not_paid";
    public const string ItemInactive = "item_inactive";
    public const string ItemGone = "item_removed";
    public const string PlateTaken = "plate_taken";
    public const string DriverAssigned = "driver_assigned";
    public const string NotTheDriver = "not_the_driver";
    public const string NoDriverOnDate = "no_driver_on_date";
    public const string EvidenceRequired = "evidence_required";
    public const string FileTypeNotAllowed = "file_type_not_allowed";
    public const string FileTooLargeCode = "file_too_large";
    public const string EmptyFileCode = "file_empty";
    public const string StorageUnavailable = "storage_unavailable";
    public const string BadAttachmentsCode = "attachments_invalid";
    public const string AlreadyReferred = "already_referred";
    public const string ExecutiveRequired = "executive_required";
    public const string PriceAboveRange = "price_above_range";
    public const string PriceExceptionPending = "price_exception_pending";
    public const string PriceExceptionRejected = "price_exception_rejected";
    public const string NotVehicle = "not_a_vehicle";
    public const string AlreadyPosted = "already_posted";
    public const string VehicleTrackingNoticeNotAccepted = "vehicle_tracking_notice_not_accepted";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    /// <summary>
    /// The project doesn't exist, or the caller may not see it. Both get the same answer, so a guessed id
    /// reveals nothing.
    /// </summary>
    public static ProblemHttpResult ProjectNotFound() => ApiProblem.NotFoundResult("The project was not found.");

    public static ProblemHttpResult NotFound(string what) => ApiProblem.NotFoundResult($"The {what} was not found.");

    public static ProblemHttpResult EquipmentNotFound() => NotFound("equipment");

    public static ProblemHttpResult AssignmentNotFound() => NotFound("assignment");

    public static ProblemHttpResult AdjustmentNotFound() => NotFound("adjustment");

    public static ProblemHttpResult ExecutiveOnly() =>
        ApiProblem.ForbiddenResult("Only a currently signed-in Executive can decide a correction.");

    /// <summary>One correction of a cost waits for a decision at a time.</summary>
    public static ProblemHttpResult AdjustmentAlreadyOpen() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, AdjustmentOpen, "A correction of this cost is already waiting for a decision.");

    public static ProblemHttpResult AdjustmentBelowZero() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, AdjustmentBelowPaid, "The corrected amount can't be negative.");

    /// <summary>A credit cost is paid once for its full amount (decision F2): once requested or paid it isn't corrected.</summary>
    public static ProblemHttpResult BeingPaid() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, CostBeingPaid,
            "This cost is paid or its payment is requested, so it can't be corrected. Void the payment or cancel the request first.");

    public static ProblemHttpResult CostNotFound() => NotFound("equipment cost");

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

    public static ProblemHttpResult AssetNoAlreadyUsed() =>
        Conflict(AssetNoTaken, "Another machine already has this asset number.");

    /// <summary>The machine has an open assignment, on this project or another one.</summary>
    public static ProblemHttpResult AlreadyAssigned() =>
        Conflict(EquipmentAssigned, "This machine is already on a project. End that assignment first.");

    /// <summary>Only a machine in service can be sent to a project.</summary>
    public static ProblemHttpResult Unavailable(string status) =>
        Conflict(EquipmentUnavailable, $"This machine is {status}. Only a machine in service can be sent to a project.");

    public static ProblemHttpResult NotOnProject() =>
        Conflict(NeverOnProject, "This machine has never been on this project, so it can't have costs here.");

    public static ProblemHttpResult InactiveItem() =>
        Conflict(ItemInactive, "This work item is inactive. Equipment costs can't be newly linked to it.");

    /// <summary>The work item was deleted by someone else between our check and our save.</summary>
    public static ProblemHttpResult ItemRemoved() =>
        Conflict(ItemGone, "The work item was removed while you were saving. Reload the items and try again.");

    /// <summary>An open payment request already covers this cost; it is paid, rejected or cancelled first.</summary>
    public static ProblemHttpResult PaymentAlreadyRequested() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PaymentRequested, "A payment request for this cost is already waiting.");

    /// <summary>The same form (same cost and row version) was sent again with different details.</summary>
    public static ProblemHttpResult KeyUsedElsewhere() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, IdempotencyKeyReused, "This payment form was already sent with different details. Reload the cost and try again.");

    public static ProblemHttpResult PaymentAlreadyRecorded() =>
        Conflict(AlreadyPaid, "This cost is already paid. Void the payment first if it was wrong.");

    public static ProblemHttpResult NoPayment() =>
        Conflict(NotPaid, "This cost has no payment to void.");

    /// <summary>Recording and fixing equipment costs needs equipment Create or equipment Edit.</summary>
    public static ProblemHttpResult RecordingNeedsCreateOrEdit() =>
        ApiProblem.ForbiddenResult("Recording an equipment cost needs equipment Create or equipment Edit.");

    public static ProblemHttpResult NotTheRecorder() =>
        ApiProblem.ForbiddenResult("Only the person who recorded this cost can change it.");

    public static ProblemHttpResult VehicleNotFound() => NotFound("vehicle");
    public static ProblemHttpResult DriverAssignmentNotFound() => NotFound("driver assignment");
    public static ProblemHttpResult ClaimNotFound() => NotFound("vehicle expense claim");
    public static ProblemHttpResult ViolationNotFound() => NotFound("vehicle violation");
    public static ProblemHttpResult AttachmentNotFound() => NotFound("attachment");

    public static ProblemHttpResult PlateAlreadyUsed() => Conflict(PlateTaken, "Another vehicle already has this plate.");

    public static ProblemHttpResult DriverAlreadyAssigned() => Conflict(DriverAssigned, "This vehicle already has a driver. End that assignment first.");

    public static ProblemHttpResult NotAVehicle() => Conflict(NotVehicle, "This equipment is not a vehicle.");

    /// <summary>A claim is made by the vehicle's driver on that day; nobody claims against another vehicle's custody.</summary>
    public static ProblemHttpResult NotTheVehiclesDriver() =>
        ApiProblem.ForbiddenResult("Only the driver assigned to this vehicle on the expense date (or a fleet manager on their behalf) can claim against its custody.");

    public static ProblemHttpResult NobodyDrove(DateOnly date) =>
        Conflict(NoDriverOnDate, $"No driver was assigned to this vehicle on {date:yyyy-MM-dd}.");

    public static ProblemHttpResult EvidenceMissing(string what) =>
        ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, EvidenceRequired, $"A claim needs {what} before it can be submitted.");

    public static ProblemHttpResult TypeNotAllowed(string allowed) =>
        ApiProblem.Create(StatusCodes.Status415UnsupportedMediaType, FileTypeNotAllowed, $"The file type is not accepted. Allowed: {allowed}.");

    public static ProblemHttpResult TooLarge(long maxBytes) =>
        ApiProblem.Create(StatusCodes.Status413PayloadTooLarge, FileTooLargeCode, $"The file is larger than the limit of {maxBytes / (1024 * 1024)} MB.");

    public static ProblemHttpResult EmptyFile() => ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, EmptyFileCode, "The file is empty.");

    public static ProblemHttpResult StorageDown() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, StorageUnavailable, "The evidence folder can't be used right now. Try again in a moment.");

    public static ProblemHttpResult BadAttachments(string title) => ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, BadAttachmentsCode, title);

    public static ProblemHttpResult ReferredAlready() => Conflict(AlreadyReferred, "This record was already referred to HR.");

    public static ProblemHttpResult NeedsExecutive() =>
        ApiProblem.ForbiddenResult("Only a currently signed-in Executive can decide this claim (escalated, or above the policy's limit).");

    public static ProblemHttpResult AboveRangeBlocked(decimal unitPrice, decimal maxPrice) =>
        Conflict(PriceAboveRange, $"The price per litre {unitPrice:0.00} is above the highest reference price {maxPrice:0.00}. The company policy refuses it.");

    public static ProblemHttpResult PriceCasePending() =>
        Conflict(PriceExceptionPending, "The price of this claim waits for the Executive's decision in Price Control.");

    public static ProblemHttpResult PriceCaseRejected() =>
        Conflict(PriceExceptionRejected, "The Executive rejected the price of this claim. It can't be approved at this price.");

    public static ProblemHttpResult PostedAlready() => Conflict(AlreadyPosted, "This claim was already posted to the vehicle's custody.");

    /// <summary>Another change to the same records held its lock for too long; safe to retry.</summary>
    public static ProblemHttpResult TryAgainLater() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy, "Another change is still running. Try again in a moment.");

    /// <summary>Positions from a phone where the driver hasn't accepted the current tracking notice: nothing is kept.</summary>
    public static ProblemHttpResult TrackingNoticeNotAccepted() =>
        Conflict(VehicleTrackingNoticeNotAccepted, "Accept the vehicle tracking notice on this phone first. Nothing was stored.");
}
