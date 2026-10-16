using AlMotaheda.Modules.Fleet.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>One equipment cost with the machine next to it, as the queries project it.</summary>
internal sealed class EquipmentCostRow
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int EquipmentId { get; init; }
    public string AssetNo { get; init; } = "";
    public string EquipmentNameAr { get; init; } = "";
    public string EquipmentNameEn { get; init; } = "";
    public DateOnly CostDate { get; init; }
    public EquipmentCostType Type { get; init; }
    public CostBasis? Basis { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? Rate { get; init; }
    public decimal? Liters { get; init; }
    public decimal? PricePerLiter { get; init; }
    public decimal Amount { get; init; }
    public string Description { get; init; } = "";
    public int? SupplierId { get; init; }
    public CostChannel Channel { get; init; }
    public string? InvoiceNo { get; init; }
    public EquipmentCostStatus Status { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public int? ReviewedBy { get; init; }
    public string? ReviewNote { get; init; }
    public DateOnly? PaidOn { get; init; }
    public PaymentMethod? PaymentMethod { get; init; }
    public string? PaymentReference { get; init; }
    public DateTime? PaymentRecordedAt { get; init; }
    public int? PaymentRecordedBy { get; init; }
    public DateTime? PaymentVoidedAt { get; init; }
    public int? PaymentVoidedBy { get; init; }
    public string? PaymentVoidReason { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>The recorded amount plus its approved adjustments: what every figure uses.</summary>
    public decimal AdjustedAmount { get; init; }

    public List<CostAdjustmentRef> Adjustments { get; init; } = [];

    public EquipmentCostSettlementRow Settlement =>
        new(Id, EquipmentId, Type, Status, Channel, CostDate, AdjustedAmount, Liters, PaidOn is not null);
}

/// <summary>One correction of a cost, as loaded with it.</summary>
internal sealed record CostAdjustmentRef(
    int Id, decimal Amount, string Reason, AdjustmentStatus Status, DateTime RequestedAt, int RequestedBy,
    DateTime? DecidedAt, int? DecidedBy, string? DecisionNote, byte[] RowVersion);

/// <summary>The filters of <c>GET /api/projects/{pid}/equipment-costs</c>, after validation.</summary>
/// <param name="RecordedBy">Set for a caller without equipment See money: only their own records.</param>
internal sealed record EquipmentCostFilter(
    EquipmentCostType? Type,
    EquipmentCostStatus? Status,
    int? EquipmentId,
    DateOnly? From,
    DateOnly? To,
    int? RecordedBy);

/// <summary>A machine's number and names, for the summary.</summary>
internal sealed record EquipmentNameRow(int Id, string AssetNo, string NameAr, string NameEn, EquipmentOwnership Ownership);

/// <summary>The equipment cost queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class EquipmentCostQueries
{
    /// <summary>
    /// Every cost of one project, flat: what every calculation needs and nothing else. <paramref name="from"/> and
    /// <paramref name="to"/> limit the cost date (inclusive). Always one project: costs of another project are
    /// never read.
    /// </summary>
    public static IQueryable<EquipmentCostSettlementRow> SettlementRows(FleetDbContext db, int projectId, DateOnly? from = null, DateOnly? to = null)
    {
        var query = db.EquipmentCosts.AsNoTracking().Where(cost => cost.ProjectId == projectId);

        if (from is { } start)
        {
            query = query.Where(cost => cost.CostDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(cost => cost.CostDate <= end);
        }

        return Settle(query);
    }

    /// <summary>The same row for one cost of the project (a correction, a payment request).</summary>
    public static IQueryable<EquipmentCostSettlementRow> SettlementRow(FleetDbContext db, int projectId, int costId) =>
        Settle(db.EquipmentCosts.AsNoTracking().Where(cost => cost.ProjectId == projectId && cost.Id == costId));

    private static IQueryable<EquipmentCostSettlementRow> Settle(IQueryable<EquipmentCost> query)
    {
        return query.Select(cost => new EquipmentCostSettlementRow(
            cost.Id,
            cost.EquipmentId,
            cost.Type,
            cost.Status,
            cost.Channel,
            cost.CostDate,
            // The adjusted amount: the recorded amount plus its approved corrections (M6).
            cost.Amount + (cost.Adjustments.Where(adjustment => adjustment.Status == AdjustmentStatus.Approved)
                .Sum(adjustment => (decimal?)adjustment.Amount) ?? 0),
            cost.Liters,
            cost.PaidOn != null));
    }

    /// <summary>The list screen: every filter, the sort (newest first) and the paging run in SQL.</summary>
    public static IQueryable<EquipmentCostRow> Filtered(FleetDbContext db, int projectId, EquipmentCostFilter filter)
    {
        var query = db.EquipmentCosts.AsNoTracking().Where(cost => cost.ProjectId == projectId);

        if (filter.RecordedBy is { } recorder)
        {
            query = query.Where(cost => cost.CreatedBy == recorder);
        }

        if (filter.Type is { } type)
        {
            query = query.Where(cost => cost.Type == type);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(cost => cost.Status == status);
        }

        if (filter.EquipmentId is { } equipmentId)
        {
            query = query.Where(cost => cost.EquipmentId == equipmentId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(cost => cost.CostDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(cost => cost.CostDate <= to);
        }

        return Project(query.OrderByDescending(cost => cost.CostDate).ThenByDescending(cost => cost.Id));
    }

    /// <summary>One cost of a project, or none when the id belongs to another project.</summary>
    public static IQueryable<EquipmentCostRow> One(FleetDbContext db, int projectId, int costId) =>
        Project(db.EquipmentCosts.AsNoTracking().Where(cost => cost.ProjectId == projectId && cost.Id == costId));

    /// <summary>The numbers and names of a few machines.</summary>
    public static IQueryable<EquipmentNameRow> Names(FleetDbContext db, IReadOnlyCollection<int> equipmentIds) =>
        db.Equipment.AsNoTracking()
            .Where(equipment => equipmentIds.Contains(equipment.Id))
            .Select(equipment => new EquipmentNameRow(equipment.Id, equipment.AssetNo, equipment.NameAr, equipment.NameEn, equipment.Ownership));

    /// <summary>True when the machine has, or had, a stay on the project: the condition for a cost there.</summary>
    public static IQueryable<EquipmentAssignment> StaysOn(FleetDbContext db, int projectId, int equipmentId) =>
        db.EquipmentAssignments.AsNoTracking()
            .Where(assignment => assignment.ProjectId == projectId && assignment.EquipmentId == equipmentId);

    private static IQueryable<EquipmentCostRow> Project(IQueryable<EquipmentCost> query) =>
        query.Select(cost => new EquipmentCostRow
        {
            Id = cost.Id,
            ProjectId = cost.ProjectId,
            EquipmentId = cost.EquipmentId,
            AssetNo = cost.Equipment.AssetNo,
            EquipmentNameAr = cost.Equipment.NameAr,
            EquipmentNameEn = cost.Equipment.NameEn,
            CostDate = cost.CostDate,
            Type = cost.Type,
            Basis = cost.Basis,
            Quantity = cost.Quantity,
            Rate = cost.Rate,
            Liters = cost.Liters,
            PricePerLiter = cost.PricePerLiter,
            Amount = cost.Amount,
            AdjustedAmount = cost.Amount + (cost.Adjustments.Where(adjustment => adjustment.Status == AdjustmentStatus.Approved)
                .Sum(adjustment => (decimal?)adjustment.Amount) ?? 0),
            Adjustments = cost.Adjustments
                .OrderBy(adjustment => adjustment.Id)
                .Select(adjustment => new CostAdjustmentRef(
                    adjustment.Id, adjustment.Amount, adjustment.Reason, adjustment.Status, adjustment.RequestedAt, adjustment.RequestedBy,
                    adjustment.DecidedAt, adjustment.DecidedBy, adjustment.DecisionNote, adjustment.RowVersion))
                .ToList(),
            Description = cost.Description,
            SupplierId = cost.SupplierId,
            Channel = cost.Channel,
            InvoiceNo = cost.InvoiceNo,
            Status = cost.Status,
            ReviewedAt = cost.ReviewedAt,
            ReviewedBy = cost.ReviewedBy,
            ReviewNote = cost.ReviewNote,
            PaidOn = cost.PaidOn,
            PaymentMethod = cost.PaymentMethod,
            PaymentReference = cost.PaymentReference,
            PaymentRecordedAt = cost.PaymentRecordedAt,
            PaymentRecordedBy = cost.PaymentRecordedBy,
            PaymentVoidedAt = cost.PaymentVoidedAt,
            PaymentVoidedBy = cost.PaymentVoidedBy,
            PaymentVoidReason = cost.PaymentVoidReason,
            CreatedAt = cost.CreatedAt,
            CreatedBy = cost.CreatedBy,
            UpdatedAt = cost.UpdatedAt,
            UpdatedBy = cost.UpdatedBy,
            RowVersion = cost.RowVersion,
        });
}
