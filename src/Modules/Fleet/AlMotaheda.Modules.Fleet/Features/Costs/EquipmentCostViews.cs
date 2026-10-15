using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>The one payment of a credit cost.</summary>
internal sealed record EquipmentCostPaymentView(DateOnly PaidOn, string Method, string? Reference, DateTime RecordedAt, FleetPerson RecordedBy);

/// <summary>The last payment that was voided as wrong: who, when and why.</summary>
internal sealed record EquipmentCostVoidView(DateTime VoidedAt, FleetPerson VoidedBy, string Reason);

/// <summary>
/// One equipment cost, as the list, the detail and every write return it. Money figures come from
/// <see cref="EquipmentCostSettlement"/>: <see cref="Recognized"/> is zero until approved, <see cref="Paid"/>
/// follows the channel, <see cref="PaymentState"/> is empty while nothing is owed.
/// </summary>
internal sealed record EquipmentCostView(
    int Id,
    int ProjectId,
    int EquipmentId,
    string AssetNo,
    string EquipmentNameAr,
    string EquipmentNameEn,
    DateOnly CostDate,
    string Type,
    string? Basis,
    decimal? Quantity,
    decimal? Rate,
    decimal? Liters,
    decimal? PricePerLiter,
    decimal Amount,
    string Description,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    string Channel,
    string? InvoiceNo,
    string Status,
    DateTime? ReviewedAt,
    FleetPerson? Reviewer,
    string? ReviewNote,
    decimal Recognized,
    decimal Paid,
    decimal Outstanding,
    string? PaymentState,
    EquipmentCostPaymentView? Payment,
    EquipmentCostVoidView? LastVoidedPayment,
    DateTime CreatedAt,
    FleetPerson Recorder,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public static EquipmentCostView From(
        EquipmentCostRow row, IReadOnlyDictionary<int, UserSummary> people, IReadOnlyDictionary<int, SupplierRef> suppliers)
    {
        var settlement = row.Settlement;
        SupplierRef? supplier = null;
        if (row.SupplierId is { } supplierId)
        {
            suppliers.TryGetValue(supplierId, out supplier);
        }

        return new EquipmentCostView(
            row.Id,
            row.ProjectId,
            row.EquipmentId,
            row.AssetNo,
            row.EquipmentNameAr,
            row.EquipmentNameEn,
            row.CostDate,
            row.Type.ToString(),
            row.Basis?.ToString(),
            row.Quantity,
            row.Rate,
            row.Liters,
            row.PricePerLiter,
            row.Amount,
            row.Description,
            row.SupplierId,
            supplier?.NameAr,
            supplier?.NameEn,
            row.Channel.ToString(),
            row.InvoiceNo,
            row.Status.ToString(),
            row.ReviewedAt,
            FleetPerson.FromOptional(row.ReviewedBy, people),
            row.ReviewNote,
            EquipmentCostSettlement.Recognized(settlement),
            EquipmentCostSettlement.Paid(settlement),
            EquipmentCostSettlement.Outstanding(settlement),
            EquipmentCostSettlement.PaymentState(settlement),
            row.PaidOn is { } paidOn
                ? new EquipmentCostPaymentView(
                    paidOn,
                    row.PaymentMethod!.Value.ToString(),
                    row.PaymentReference,
                    row.PaymentRecordedAt!.Value,
                    FleetPerson.From(row.PaymentRecordedBy!.Value, people))
                : null,
            row.PaymentVoidedAt is { } voidedAt
                ? new EquipmentCostVoidView(voidedAt, FleetPerson.From(row.PaymentVoidedBy!.Value, people), row.PaymentVoidReason!)
                : null,
            row.CreatedAt,
            FleetPerson.From(row.CreatedBy, people),
            row.UpdatedAt,
            row.UpdatedBy,
            RowVersions.Format(row.RowVersion));
    }

    /// <summary>Builds the views of a page with one name lookup for every person and one for every supplier on it.</summary>
    public static async Task<IReadOnlyList<EquipmentCostView>> FromAsync(
        IReadOnlyList<EquipmentCostRow> rows, IUserDirectory users, ISupplierDirectory suppliers, CancellationToken cancellationToken)
    {
        var people = await Lookups.UsersAsync(
            users,
            rows.SelectMany(row => new[] { row.CreatedBy, row.ReviewedBy, row.PaymentRecordedBy, row.PaymentVoidedBy }),
            cancellationToken);
        var supplierNames = await Lookups.SuppliersAsync(suppliers, rows.Select(row => row.SupplierId), cancellationToken);

        return rows.Select(row => From(row, people, supplierNames)).ToList();
    }

    /// <summary>Reads one cost of a project back after a write, with its names.</summary>
    public static async Task<EquipmentCostView?> LoadAsync(
        FleetDbContext db, IUserDirectory users, ISupplierDirectory suppliers, int projectId, int costId, CancellationToken cancellationToken)
    {
        var row = await EquipmentCostQueries.One(db, projectId, costId).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (await FromAsync([row], users, suppliers, cancellationToken))[0];
    }
}
