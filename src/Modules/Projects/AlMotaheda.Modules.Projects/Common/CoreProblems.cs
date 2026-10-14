using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Error responses of the first part of the workspace: suppliers, project items, expenses and supplier payments.
/// Codes shared with the other parts live in <see cref="WorkspaceProblems"/> and <see cref="ProjectProblems"/>.
/// </summary>
internal static class CoreProblems
{
    public const string SupplierExists = "supplier_exists";
    public const string ItemNameTaken = "item_name_taken";
    public const string ItemHasRecords = "item_has_records";
    public const string OverAllocation = "over_allocation";
    public const string PaymentAlreadyVoided = "payment_already_voided";

    public static ProblemHttpResult SupplierNotFound() => WorkspaceProblems.NotFound("supplier");

    public static ProblemHttpResult ItemNotFound() => WorkspaceProblems.NotFound("project item");

    public static ProblemHttpResult ExpenseNotFound() => WorkspaceProblems.NotFound("record");

    public static ProblemHttpResult PaymentNotFound() => WorkspaceProblems.NotFound("payment");

    public static ProblemHttpResult SupplierNameTaken() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, SupplierExists, "A supplier with this English name already exists.");

    public static ProblemHttpResult ItemNameAlreadyUsed() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ItemNameTaken, "This project already has an item with this name.");

    /// <summary>Records are linked to the item, so it can only be deactivated.</summary>
    public static ProblemHttpResult ItemStillHasRecords() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            ItemHasRecords,
            "Records are linked to this item, so it can't be deleted. Deactivate it instead.");

    /// <summary>Only an Executive or the project's manager changes the project's items.</summary>
    public static ProblemHttpResult ItemsManagedByManager() =>
        ApiProblem.ForbiddenResult("Only an Executive or this project's manager can change its items.");

    public static ProblemHttpResult NotTheRecorder() =>
        ApiProblem.ForbiddenResult("Only the person who recorded this record can change it.");

    public static ProblemHttpResult LinkNeedsEditOrApprove() =>
        ApiProblem.ForbiddenResult("Linking a record to an item needs projects Edit or projects Approve.");

    public static ProblemHttpResult Overallocated(int expenseId) =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            OverAllocation,
            $"The allocation to record {expenseId} is more than what is still unpaid on it.");

    public static ProblemHttpResult KeyUsedElsewhere() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            WorkspaceProblems.IdempotencyKeyReused,
            "This idempotency key was already used for a different payment. Make a new key for a new payment.");

    public static ProblemHttpResult AlreadyVoided() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PaymentAlreadyVoided, "This payment is already voided.");
}
