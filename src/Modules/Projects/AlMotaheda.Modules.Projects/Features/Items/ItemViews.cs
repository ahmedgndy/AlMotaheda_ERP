using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>One row of the project items table: the item and its figures from <see cref="ItemsLedger"/>.</summary>
/// <remarks>
/// The money fields are the item's <b>own</b> records. For a main item, <see cref="WithSubItems"/> adds its
/// sub-items' records; it is <c>null</c> on a sub-item. The table's totals are sums of the own figures, so a
/// sub-item's record is counted once, never again through its main item.
/// </remarks>
/// <param name="Level">1 for a main item, 2 for a sub-item.</param>
internal sealed record ItemRow(
    int Id,
    string NameAr,
    string NameEn,
    string Category,
    string Unit,
    string? Description,
    string? Notes,
    string Status,
    DateTime? DeactivatedAt,
    int? DeactivatedBy,
    int SupplierRecordCount,
    int SupplierCount,
    int SubcontractRecordCount,
    int SubcontractorCount,
    int LaborRecordCount,
    int WorkerCount,
    int LinkedRecordCount,
    decimal SupplierCost,
    decimal SubcontractorCost,
    decimal SupplierAndSubcontractorCost,
    decimal LaborCost,
    decimal TotalCost,
    decimal SupplierPaid,
    decimal SubcontractorPaid,
    decimal LaborPaid,
    decimal Paid,
    decimal Outstanding,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion,
    int? ParentItemId,
    int Level,
    int SubItemCount,
    ItemFiguresView? WithSubItems,
    int DirectRecordCount = 0,
    decimal DirectCost = 0,
    decimal DirectPaid = 0,
    decimal SupplierOutstanding = 0,
    decimal SubcontractorOutstanding = 0,
    decimal LaborOutstanding = 0,
    decimal DirectOutstanding = 0,
    int EquipmentRecordCount = 0,
    int EquipmentCount = 0,
    decimal EquipmentCost = 0,
    decimal EquipmentPaid = 0,
    decimal EquipmentOutstanding = 0,
    ItemPlanView? Plan = null)
{
    /// <param name="withSubItems">The roll-up of a main item (<see cref="ItemTree.WithSubItems"/>); <c>null</c> for a sub-item.</param>
    public static ItemRow From(ItemHeader item, ItemFigures figures, int subItemCount, ItemFigures? withSubItems) => new(
        item.Id,
        item.NameAr,
        item.NameEn,
        item.Category,
        item.Unit,
        item.Description,
        item.Notes,
        item.Status.ToString(),
        item.DeactivatedAt,
        item.DeactivatedBy,
        figures.SupplierRecordCount,
        figures.SupplierCount,
        figures.SubcontractRecordCount,
        figures.SubcontractorCount,
        figures.LaborRecordCount,
        figures.WorkerCount,
        figures.LinkedRecordCount,
        figures.SupplierCost,
        figures.SubcontractorCost,
        figures.SupplierAndSubcontractorCost,
        figures.LaborCost,
        figures.TotalCost,
        figures.SupplierPaid,
        figures.SubcontractorPaid,
        figures.LaborPaid,
        figures.Paid,
        figures.Outstanding,
        item.CreatedAt,
        item.CreatedBy,
        item.UpdatedAt,
        item.UpdatedBy,
        RowVersions.Format(item.RowVersion),
        item.ParentItemId,
        item.IsMainItem ? 1 : 2,
        subItemCount,
        withSubItems is null ? null : ItemFiguresView.From(withSubItems),
        figures.DirectRecordCount,
        figures.DirectCost,
        figures.DirectPaid,
        figures.SupplierCost - figures.SupplierPaid,
        figures.SubcontractorCost - figures.SubcontractorPaid,
        figures.LaborCost - figures.LaborPaid,
        figures.DirectCost - figures.DirectPaid,
        figures.EquipmentRecordCount,
        figures.EquipmentCount,
        figures.EquipmentCost,
        figures.EquipmentPaid,
        figures.EquipmentCost - figures.EquipmentPaid);

    public static ItemHeader Header(ProjectItem item) => new(
        item.Id, item.NameAr, item.NameEn, item.Category, item.Unit, item.Description, item.Notes, item.Status,
        item.DeactivatedAt, item.DeactivatedBy, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, item.RowVersion,
        item.ParentItemId);

    /// <summary>The row of a tracked item after a change, with the figures of the ledger loaded after the save.</summary>
    public static ItemRow From(ProjectItem item, ItemsLedgerData data) => data.RowOf(Header(item));

    /// <summary>The row of an item that can't have records yet (just created).</summary>
    public static ItemRow New(ProjectItem item) =>
        From(Header(item), ItemFigures.Empty, 0, item.ParentItemId is null ? ItemFigures.Empty : null);
}

/// <summary>The figures of a group (all items, or the not-linked group), as the items screens return them.</summary>
internal sealed record ItemFiguresView(
    int SupplierRecordCount,
    int SupplierCount,
    int SubcontractRecordCount,
    int SubcontractorCount,
    int LaborRecordCount,
    int WorkerCount,
    int LinkedRecordCount,
    decimal SupplierCost,
    decimal SubcontractorCost,
    decimal SupplierAndSubcontractorCost,
    decimal LaborCost,
    decimal TotalCost,
    decimal SupplierPaid,
    decimal SubcontractorPaid,
    decimal LaborPaid,
    decimal Paid,
    decimal Outstanding,
    int DirectRecordCount = 0,
    decimal DirectCost = 0,
    decimal DirectPaid = 0,
    decimal SupplierOutstanding = 0,
    decimal SubcontractorOutstanding = 0,
    decimal LaborOutstanding = 0,
    decimal DirectOutstanding = 0,
    int EquipmentRecordCount = 0,
    int EquipmentCount = 0,
    decimal EquipmentCost = 0,
    decimal EquipmentPaid = 0,
    decimal EquipmentOutstanding = 0)
{
    public static ItemFiguresView From(ItemFigures figures) => new(
        figures.SupplierRecordCount,
        figures.SupplierCount,
        figures.SubcontractRecordCount,
        figures.SubcontractorCount,
        figures.LaborRecordCount,
        figures.WorkerCount,
        figures.LinkedRecordCount,
        figures.SupplierCost,
        figures.SubcontractorCost,
        figures.SupplierAndSubcontractorCost,
        figures.LaborCost,
        figures.TotalCost,
        figures.SupplierPaid,
        figures.SubcontractorPaid,
        figures.LaborPaid,
        figures.Paid,
        figures.Outstanding,
        figures.DirectRecordCount,
        figures.DirectCost,
        figures.DirectPaid,
        figures.SupplierCost - figures.SupplierPaid,
        figures.SubcontractorCost - figures.SubcontractorPaid,
        figures.LaborCost - figures.LaborPaid,
        figures.DirectCost - figures.DirectPaid,
        figures.EquipmentRecordCount,
        figures.EquipmentCount,
        figures.EquipmentCost,
        figures.EquipmentPaid,
        figures.EquipmentCost - figures.EquipmentPaid);
}

/// <summary>
/// What is planned and committed for a row of the items table (with its sub-items for a main item), next to its
/// actual cost. Never added to a cost: a budget is a plan, a commitment is signed or ordered but not executed.
/// </summary>
/// <param name="Budgeted">Lines of the approved budget on the item (and its sub-items); 0 without an approved budget.</param>
/// <param name="RemainingBudget">Budgeted − actual cost; <c>null</c> when nothing is budgeted.</param>
/// <param name="SubcontractCommitted">Contract lines on the item not executed yet (contracts not completed).</param>
/// <param name="OrdersCommitted">Purchase order lines on the item not invoiced yet (Procurement).</param>
/// <param name="PendingReviewCount">Supplier and direct records on the item waiting for review: not cost yet.</param>
/// <param name="PriceWarningCount">Records on the item bought above their reference price range.</param>
internal sealed record ItemPlanView(
    decimal Budgeted,
    decimal? RemainingBudget,
    bool OverBudget,
    decimal SubcontractCommitted,
    decimal OrdersCommitted,
    int PendingReviewCount,
    int PriceWarningCount);

/// <summary>Where the project's recognized cost comes from, so the screen can show that the parts add up.</summary>
/// <param name="OtherCosts">Site expenses outside the items screens plus equipment costs (Fleet) on no work item.</param>
internal sealed record ItemsReconciliation(decimal ItemsTotalCost, decimal UnallocatedTotalCost, decimal OtherCosts, decimal ProjectRecognizedCost);

/// <summary>One supplier record on an item, or in the not-linked group.</summary>
/// <param name="Description">The item name of a supply, or the description of a site expense.</param>
internal sealed record SupplierLine(
    int ExpenseId,
    string Kind,
    DateOnly Date,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    string? SupplierKind,
    string? Description,
    string? InvoiceNo,
    string? DeliveryNoteNo,
    decimal? Quantity,
    string? Unit,
    decimal? UnitPrice,
    decimal Amount,
    string Channel,
    string Status,
    decimal Paid,
    decimal Outstanding,
    string? PaymentState,
    IReadOnlyList<ExpensePaymentView> Payments,
    string RowVersion)
{
    public static SupplierLine From(ExpenseRow row)
    {
        var settlement = row.Settlement;

        return new SupplierLine(
            row.Id,
            row.Kind.ToString(),
            row.ExpenseDate,
            row.SupplierId,
            row.SupplierNameAr,
            row.SupplierNameEn,
            row.SupplierKind?.ToString(),
            row.Kind == ExpenseKind.Supply ? row.ItemName : row.Description,
            row.InvoiceNo,
            row.DeliveryNoteNo,
            row.Quantity,
            row.Unit,
            row.UnitPrice,
            row.Amount,
            row.Channel.ToString(),
            row.Status.ToString(),
            ExpenseSettlement.Paid(settlement),
            ExpenseSettlement.Outstanding(settlement),
            ExpenseSettlement.PaymentState(settlement),
            row.Payments.Select(ExpensePaymentView.From).ToList(),
            RowVersions.Format(row.RowVersion));
    }
}

/// <summary>
/// The detail of one item, or of the "not linked yet" group (<see cref="Item"/> is then <c>null</c>): the approved
/// lines from every source, the lines still waiting for review, and the same figures as the table row.
/// </summary>
/// <param name="SubItems">A main item's sub-items, as rows of the table (own figures each). Empty otherwise.</param>
/// <param name="WithSubItems">A main item's figures with its sub-items; <c>null</c> for a sub-item and the not-linked group.</param>
/// <param name="EquipmentLines">
/// The item's approved equipment costs. Always empty for the not-linked group: an equipment cost on no item is a
/// cost of the whole site, not a record waiting for its item.
/// </param>
internal sealed record ItemDetail(
    ItemRow? Item,
    IReadOnlyList<SupplierLine> SupplierLines,
    IReadOnlyList<SubcontractItemLine> SubcontractLines,
    IReadOnlyList<LaborItemLine> LaborLines,
    IReadOnlyList<SupplierLine> PendingSupplierLines,
    ItemFiguresView Totals,
    IReadOnlyList<ItemRow> SubItems,
    ItemFiguresView? WithSubItems,
    IReadOnlyList<EquipmentItemLine> EquipmentLines);
