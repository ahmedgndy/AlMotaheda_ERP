using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Procurement.Features;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Integration;

/// <summary>
/// Tells the project screens what is committed on open purchase orders: ordered but not invoiced yet, at the PO
/// price, in the Materials group. Only <c>Committed</c> is reported: invoiced purchases are already Projects' supply
/// records, so the actual cost is never counted twice.
/// </summary>
internal sealed class CommittedCostSource(ProcurementDbContext db) : IProjectCostSource
{
    public string Group => CostGroups.Materials;

    /// <remarks>Committed cost is a position today, so the date range doesn't apply to it.</remarks>
    public async Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var lines = await db.PurchaseOrderLines.AsNoTracking()
            .Join(db.PurchaseOrders.Where(order => order.ProjectId == projectId && order.Status != PurchaseOrderStatus.Cancelled),
                line => line.OrderId, order => order.Id, (line, _) => new { line.Id, line.Quantity, line.UnitPrice })
            .ToListAsync(cancellationToken);
        if (lines.Count == 0)
        {
            return ProjectCostFigures.Empty;
        }

        var invoiced = await ChainQuantities.InvoicedAsync(db, lines.Select(line => line.Id).ToList(), cancellationToken);
        var committed = lines.Sum(line => ProcurementRules.Committed(line.Quantity, invoiced.GetValueOrDefault(line.Id), line.UnitPrice));
        return ProjectCostFigures.Empty with { Committed = committed };
    }
}
