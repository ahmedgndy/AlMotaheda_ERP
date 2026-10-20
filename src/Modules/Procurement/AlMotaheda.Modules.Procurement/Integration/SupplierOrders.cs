using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Procurement.Features;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Integration;

/// <summary>Procurement's answer to <see cref="ISupplierOrders"/>: order lines with their received and invoiced quantities.</summary>
internal sealed class SupplierOrders(ProcurementDbContext db) : ISupplierOrders
{
    public async Task<IReadOnlyList<SupplierOrderLine>> LinesAsync(int? projectId, int? supplierId, CancellationToken cancellationToken)
    {
        if (projectId is null && supplierId is null)
        {
            return [];
        }

        var orders = db.PurchaseOrders.AsNoTracking().AsQueryable();
        if (projectId is { } project)
        {
            orders = orders.Where(order => order.ProjectId == project);
        }

        if (supplierId is { } supplier)
        {
            orders = orders.Where(order => order.SupplierId == supplier);
        }

        var lines = await db.PurchaseOrderLines.AsNoTracking()
            .Join(orders, line => line.OrderId, order => order.Id, (line, order) => new
            {
                line.Id,
                line.OrderId,
                order.Number,
                order.ProjectId,
                order.SupplierId,
                order.Status,
                order.ExpectedDelivery,
                order.CreatedAt,
                line.ProjectItemId,
                line.Description,
                line.Unit,
                line.Quantity,
                line.UnitPrice,
            })
            .OrderBy(line => line.OrderId)
            .ThenBy(line => line.Id)
            .ToListAsync(cancellationToken);
        if (lines.Count == 0)
        {
            return [];
        }

        var ids = lines.Select(line => line.Id).ToList();
        var received = await ChainQuantities.ReceivedAsync(db, ids, cancellationToken);
        var invoiced = await ChainQuantities.InvoicedAsync(db, ids, cancellationToken);

        return lines.Select(line =>
            {
                var invoicedQuantity = invoiced.GetValueOrDefault(line.Id);
                var committed = line.Status == PurchaseOrderStatus.Cancelled ? 0 : ProcurementRules.Committed(line.Quantity, invoicedQuantity, line.UnitPrice);
                return new SupplierOrderLine(
                    line.OrderId, line.Number, line.ProjectId, line.SupplierId, line.Id, line.ProjectItemId, line.Description, line.Unit, line.Quantity,
                    line.UnitPrice, ProcurementRules.Amount(line.Quantity, line.UnitPrice), received.GetValueOrDefault(line.Id), invoicedQuantity,
                    committed, line.Status.ToString(), line.ExpectedDelivery, DateOnly.FromDateTime(line.CreatedAt));
            })
            .ToList();
    }
}
