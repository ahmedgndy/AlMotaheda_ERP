using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Procurement.Common;

/// <summary>The Procurement module's error responses. Shared codes come from <see cref="ApiProblem"/>.</summary>
internal static class ProcurementProblems
{
    public const string InvalidStatus = "invalid_status";
    public const string OwnRecord = "own_record";
    public const string ProjectCompleted = "project_completed";
    public const string NotProjectManager = "not_project_manager";
    public const string AccountantStep = "accountant_step";
    public const string ExecutiveOnly = "executive_only";
    public const string QuotationsPending = "quotations_pending";
    public const string QuotationsExist = "quotations_exist";
    public const string NotCheapest = "reason_required";
    public const string ExceedsRequisition = "exceeds_requisition";
    public const string BelowReceived = "below_received";
    public const string PriceAlreadyInvoiced = "price_already_invoiced";
    public const string ExceedsOrdered = "exceeds_ordered";
    public const string ExceedsReceived = "exceeds_received";
    public const string InvoiceExists = "invoice_exists";
    public const string SupplierInactive = "supplier_inactive";
    public const string NotPostable = "not_postable";
    public const string NotOnProject = "not_project_member";
    public const string HasReceipts = "has_receipts";
    public const string Busy = "busy";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    public static ValidationProblem Validation(string field, string message)
    {
        var errors = new ValidationErrors();
        errors.Add(field, message);
        return Validation(errors);
    }

    public static ProblemHttpResult NotFound(string what) => ApiProblem.NotFoundResult($"The {what} was not found.");

    public static ProblemHttpResult ProjectNotFound() => NotFound("project");

    public static ProblemHttpResult WrongStatus(string title) => Conflict(InvalidStatus, title);

    public static ProblemHttpResult CompletedProject() =>
        Conflict(ProjectCompleted, "A completed project can only be changed by an Executive.");

    public static ProblemHttpResult OwnRequisition() =>
        Conflict(OwnRecord, "You made this requisition, so someone else must approve it.");

    public static ProblemHttpResult ManagerFirst() =>
        Conflict(NotProjectManager, "The first approval is the project manager's.");

    public static ProblemHttpResult AccountantSecond() =>
        Conflict(AccountantStep, "The second approval is an Accountant's.");

    public static ProblemHttpResult Executive() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, ExecutiveOnly, "Only an active Executive can do this.");

    public static ProblemHttpResult WaitingForQuotations() =>
        Conflict(QuotationsPending, "The Executive asked for quotations on this requisition. Order from the selected quotation once it is chosen.");

    public static ProblemHttpResult AlreadyOrdered() =>
        Conflict(QuotationsExist, "Quotations can only be asked for before the requisition has a purchase order, once per requisition.");

    public static ProblemHttpResult ReasonForNotCheapest() =>
        ApiProblem.Create(StatusCodes.Status400BadRequest, NotCheapest, "This quotation isn't the cheapest: give a reason.");

    public static ProblemHttpResult MoreThanRequested(string line) =>
        Conflict(ExceedsRequisition, $"Line {line}: more than the requisition still needs.");

    public static ProblemHttpResult LessThanReceived(string line) =>
        Conflict(BelowReceived, $"Line {line}: can't order less than was already received.");

    public static ProblemHttpResult PriceLocked(string line) =>
        Conflict(PriceAlreadyInvoiced, $"Line {line}: its price can't change once it is invoiced.");

    public static ProblemHttpResult MoreThanOrdered(string line) =>
        Conflict(ExceedsOrdered, $"Line {line}: more than is still due on the order.");

    public static ProblemHttpResult MoreThanReceived(string line) =>
        Conflict(ExceedsReceived, $"Line {line}: more than was received and not invoiced yet.");

    public static ProblemHttpResult DuplicateInvoice() =>
        Conflict(InvoiceExists, "This supplier invoice number is already recorded.");

    public static ProblemHttpResult InactiveSupplier() =>
        Conflict(SupplierInactive, "The supplier is inactive or unknown.");

    public static ProblemHttpResult CannotPost(string reason) => Conflict(NotPostable, reason);

    public static ProblemHttpResult NotAMember() =>
        ApiProblem.Create(StatusCodes.Status403Forbidden, NotOnProject, "Goods are received by a member of the project's team.");

    public static ProblemHttpResult Received() =>
        Conflict(HasReceipts, "Goods were received on this order: revise the remaining quantities instead of cancelling.");

    public static ProblemHttpResult TryAgainLater() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy, "The project's purchasing is being changed by someone else. Try again in a moment.");

    private static ProblemHttpResult Conflict(string code, string title) => ApiProblem.Create(StatusCodes.Status409Conflict, code, title);
}
