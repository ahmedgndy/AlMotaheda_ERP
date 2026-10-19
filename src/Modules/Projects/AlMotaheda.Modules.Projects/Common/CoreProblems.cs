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
    public const string SupplierTaxNumberExists = "supplier_tax_number_exists";
    public const string ItemNameTaken = "item_name_taken";
    public const string ItemHasRecords = "item_has_records";
    public const string OverAllocation = "over_allocation";
    public const string PaymentAlreadyVoided = "payment_already_voided";
    public const string EquipmentCostsElsewhere = "use_equipment_costs";
    public const string ItemGone = "item_removed";
    public const string AdjustmentOpen = "adjustment_open";
    public const string AdjustmentBelowPaid = "adjustment_below_paid";
    public const string ItemParentIsSubItem = "item_parent_is_sub_item";
    public const string ItemHasSubItems = "item_has_sub_items";
    public const string ItemHasActiveSubItems = "item_has_active_sub_items";
    public const string ItemParentInactive = "item_parent_inactive";

    public static ProblemHttpResult SupplierNotFound() => WorkspaceProblems.NotFound("supplier");

    public static ProblemHttpResult ItemNotFound() => WorkspaceProblems.NotFound("project item");

    public static ProblemHttpResult ExpenseNotFound() => WorkspaceProblems.NotFound("record");

    public static ProblemHttpResult AdjustmentNotFound() => WorkspaceProblems.NotFound("adjustment");

    /// <summary>One correction of a record waits for a decision at a time.</summary>
    public static ProblemHttpResult AdjustmentAlreadyOpen() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, AdjustmentOpen, "A correction of this record is already waiting for a decision.");

    /// <summary>The corrected amount would be negative, or below what is paid or held by payment requests.</summary>
    public static ProblemHttpResult AdjustmentBelowPaidAmount(decimal floor) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, AdjustmentBelowPaid,
            $"The corrected amount can't go below {floor:0.00}: that much is already paid or requested, or it would be negative.");

    public static ProblemHttpResult PaymentNotFound() => WorkspaceProblems.NotFound("payment");

    public static ProblemHttpResult SupplierNameTaken() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, SupplierExists, "A supplier with this Arabic or English name already exists.");

    public static ProblemHttpResult SupplierTaxNumberTaken() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, SupplierTaxNumberExists, "A supplier with this tax number already exists.");

    public static ProblemHttpResult ItemNameAlreadyUsed() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ItemNameTaken, "This project already has an item with this name at this level.");

    /// <summary>Two levels only: a sub-item can't be the parent of another item.</summary>
    public static ProblemHttpResult ParentIsSubItem() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ItemParentIsSubItem, "A sub-item can't have sub-items. Choose a main item as the parent.");

    /// <summary>A main item with sub-items can't become a sub-item, and can't be deleted.</summary>
    public static ProblemHttpResult StillHasSubItems() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ItemHasSubItems, "This item has sub-items. Move or delete them first.");

    /// <summary>A main item is deactivated after its sub-items, so no active sub-item hangs under an inactive item.</summary>
    public static ProblemHttpResult StillHasActiveSubItems() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ItemHasActiveSubItems, "Deactivate this item's sub-items first.");

    /// <summary>A sub-item is added, moved or reactivated only under an active main item.</summary>
    public static ProblemHttpResult ParentInactive() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ItemParentInactive, "The main item is inactive. Reactivate it first.");

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

    public static ProblemHttpResult ExpenseKeyUsedElsewhere() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            WorkspaceProblems.IdempotencyKeyReused,
            "This idempotency key was already used for another record. Make a new key for a new record.");

    public static ProblemHttpResult AlreadyVoided() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PaymentAlreadyVoided, "This payment is already voided.");

    /// <summary>Equipment costs live in Fleet only, so the same fuel or rental is never counted twice (decision F3).</summary>
    public static ProblemHttpResult UseEquipmentCosts() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            EquipmentCostsElsewhere,
            "Equipment fuel, rental and maintenance are recorded in Site equipment, not as a project expense.");

    /// <summary>The project item was deleted by someone else between our check and our save.</summary>
    public static ProblemHttpResult ItemRemoved() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            ItemGone,
            "The project item was removed while you were saving. Reload the items and try again.");
}
