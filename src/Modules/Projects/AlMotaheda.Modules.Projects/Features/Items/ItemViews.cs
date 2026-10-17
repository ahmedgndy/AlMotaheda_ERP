using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>One row of the project items table: the item and its figures from <see cref="ItemsLedger"/>.</summary>
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
    int EquipmentRecordCount,
    decimal EquipmentCost,
    int StaffRecordCount,
    decimal StaffCost,
    decimal TotalCost,
    decimal SupplierPaid,
    decimal SubcontractorPaid,
    decimal LaborPaid,
    decimal EquipmentPaid,
    decimal StaffPaid,
    decimal Paid,
    decimal Outstanding,
    int? MasterItemId,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public static ItemRow From(ItemHeader item, ItemFigures figures) => new(
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
        figures.EquipmentRecordCount,
        figures.EquipmentCost,
        figures.StaffRecordCount,
        figures.StaffCost,
        figures.TotalCost,
        figures.SupplierPaid,
        figures.SubcontractorPaid,
        figures.LaborPaid,
        figures.EquipmentPaid,
        figures.StaffPaid,
        figures.Paid,
        figures.Outstanding,
        item.MasterItemId,
        item.CreatedAt,
        item.CreatedBy,
        item.UpdatedAt,
        item.UpdatedBy,
        RowVersions.Format(item.RowVersion));

    public static ItemRow From(ProjectItem item, ItemFigures figures) => From(
        new ItemHeader(
            item.Id, item.NameAr, item.NameEn, item.Category, item.Unit, item.Description, item.Notes, item.Status,
            item.DeactivatedAt, item.DeactivatedBy, item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.UpdatedBy, item.RowVersion,
            item.MasterItemId),
        figures);
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
    int EquipmentRecordCount,
    decimal EquipmentCost,
    int StaffRecordCount,
    decimal StaffCost,
    decimal TotalCost,
    decimal SupplierPaid,
    decimal SubcontractorPaid,
    decimal LaborPaid,
    decimal EquipmentPaid,
    decimal StaffPaid,
    decimal Paid,
    decimal Outstanding)
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
        figures.EquipmentRecordCount,
        figures.EquipmentCost,
        figures.StaffRecordCount,
        figures.StaffCost,
        figures.TotalCost,
        figures.SupplierPaid,
        figures.SubcontractorPaid,
        figures.LaborPaid,
        figures.EquipmentPaid,
        figures.StaffPaid,
        figures.Paid,
        figures.Outstanding);
}

/// <summary>Where the project's recognized cost comes from, so the screen can show that the parts add up.</summary>
/// <param name="OtherCosts">Site expenses outside the items screens.</param>
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
/// lines from the three sources, the lines still waiting for review, and the same figures as the table row.
/// </summary>
/// <param name="EquipmentLines">Approved equipment costs (Fleet) on the item.</param>
/// <param name="StaffLines">Approved staff salary allocations (HR) on the item.</param>
/// <param name="Components">The item's cost by planned component; <c>null</c> for the not-linked group.</param>
internal sealed record ItemDetail(
    ItemRow? Item,
    IReadOnlyList<SupplierLine> SupplierLines,
    IReadOnlyList<SubcontractItemLine> SubcontractLines,
    IReadOnlyList<LaborItemLine> LaborLines,
    IReadOnlyList<SupplierLine> PendingSupplierLines,
    ItemFiguresView Totals,
    IReadOnlyList<ItemSourceLine> EquipmentLines,
    IReadOnlyList<ItemSourceLine> StaffLines,
    ComponentBreakdownView? Components);
