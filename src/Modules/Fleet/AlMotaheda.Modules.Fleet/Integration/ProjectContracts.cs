using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>
/// The equipment cost group of a project, for the overview, the budget comparison and the reports. Recognized = approved equipment costs in the period; paid = custody at
/// approval and credit once its payment is recorded; pending review = recorded costs; committed = 0.
/// </summary>
/// <remarks>
/// Only this project's rows are read, flat, and the arithmetic is <see cref="EquipmentCostSettlement.Figures"/>, the
/// same rule as the equipment screens (decision G5). A cost is never split over items (F1): it names one work item
/// or none, and <see cref="EquipmentItemCosts"/> reports the same rows by item.
/// </remarks>
internal sealed class EquipmentCostSource(FleetDbContext db) : IProjectCostSource
{
    public string Group => CostGroups.Equipment;

    public async Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var rows = await EquipmentCostQueries.SettlementRows(db, projectId, from, to).ToListAsync(cancellationToken);
        var claims = await VehicleExpenseSettlement.Rows(db, projectId, from, to).ToListAsync(cancellationToken);
        return EquipmentCostSettlement.Figures(rows.Concat(claims));
    }
}

/// <summary>
/// Posted vehicle expense claims as settlement rows of the project the vehicle was on at the expense date: a claim is
/// recognised and paid (from the vehicle's custody) once it is approved and posted; a submitted one is pending
/// review; a reversed posting counts nowhere. On no work item (vehicles serve the whole site).
/// </summary>
internal static class VehicleExpenseSettlement
{
    public static IQueryable<EquipmentCostSettlementRow> Rows(FleetDbContext db, int projectId, DateOnly? from = null, DateOnly? to = null)
    {
        var query = db.VehicleExpenses.AsNoTracking()
            .Where(claim => claim.ProjectId == projectId && claim.PostingStatus != VehiclePostingStatus.Reversed
                            && (claim.Status == VehicleExpenseStatus.Approved || claim.Status == VehicleExpenseStatus.Submitted || claim.Status == VehicleExpenseStatus.Escalated));
        if (from is { } start)
        {
            query = query.Where(claim => claim.ExpenseDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(claim => claim.ExpenseDate <= end);
        }

        return query.Select(claim => new EquipmentCostSettlementRow(
            claim.Id,
            claim.VehicleId,
            claim.Type == VehicleExpenseType.Fuel ? EquipmentCostType.Fuel
                : claim.Type == VehicleExpenseType.Maintenance ? EquipmentCostType.Maintenance
                : claim.Type == VehicleExpenseType.Repair ? EquipmentCostType.Repair : EquipmentCostType.Other,
            claim.Status == VehicleExpenseStatus.Approved ? EquipmentCostStatus.Approved : EquipmentCostStatus.Recorded,
            CostChannel.Custody,
            claim.ExpenseDate,
            claim.Amount,
            claim.FuelQuantity,
            claim.Status == VehicleExpenseStatus.Approved,
            null));
    }
}

/// <summary>
/// The Fleet answer to <see cref="IEquipmentItemCosts"/>: approved equipment costs per project work item, for the
/// Projects items screens. Projects never reads this module's tables.
/// </summary>
/// <remarks>
/// The same flat rows and the same arithmetic as <see cref="EquipmentCostSource"/>
/// (<see cref="EquipmentCostSettlement"/>), grouped by work item: the item totals and the total of the costs on no
/// item add up to the Equipment group, so linking a cost to an item moves it and never counts it twice.
/// </remarks>
internal sealed class EquipmentItemCosts(FleetDbContext db) : IEquipmentItemCosts
{
    public async Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken)
    {
        var rows = await EquipmentCostQueries.SettlementRows(db, projectId).ToListAsync(cancellationToken);
        var claims = await VehicleExpenseSettlement.Rows(db, projectId).ToListAsync(cancellationToken);
        return EquipmentCostSettlement.ItemTotals(rows.Concat(claims));
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<EquipmentItemLine>>> LinesByItemAsync(
        int projectId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<EquipmentItemLine>>();
        }

        var rows = await EquipmentCostQueries.ApprovedOnItems(db, projectId, itemIds.Distinct().ToArray()).ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ProjectItemId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<EquipmentItemLine>)group.Select(ToLine).ToList());
    }

    public Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken) =>
        EquipmentCostQueries.LinkedTo(db, projectId, itemId).AnyAsync(cancellationToken);

    /// <summary>Fuel is litres at a price per litre; a day, month or hour basis is a quantity at a rate; a lump sum has neither.</summary>
    private static EquipmentItemLine ToLine(EquipmentItemCostRow row)
    {
        var settlement = row.Settlement;
        var paid = EquipmentCostSettlement.Paid(settlement);
        var fuel = row.Type == EquipmentCostType.Fuel;

        return new EquipmentItemLine(
            row.Id,
            row.CostDate,
            row.EquipmentId,
            row.AssetNo,
            row.EquipmentNameAr,
            row.EquipmentNameEn,
            row.Type.ToString(),
            row.Description,
            fuel ? row.Liters : row.Quantity,
            fuel ? MeasurementUnits.Liter : row.Basis is { } basis && basis != CostBasis.Lump ? basis.ToString() : null,
            fuel ? row.PricePerLiter : row.Rate,
            row.AdjustedAmount,
            paid,
            row.AdjustedAmount - paid,
            PaymentStates.Of(row.AdjustedAmount, paid),
            row.Channel.ToString(),
            row.InvoiceNo,
            row.SupplierId);
    }
}
