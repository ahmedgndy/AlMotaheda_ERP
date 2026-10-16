using AlMotaheda.Modules.Procurement.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Features;

/// <summary>The quantity and price rules of the document chain, with no database access (12-procurement-design.md §3).</summary>
internal static class ProcurementRules
{
    /// <summary>What a PR line still needs: approved quantity − ordered on orders not cancelled.</summary>
    public static decimal StillNeeded(decimal requested, decimal ordered) => Math.Max(0, requested - ordered);

    /// <summary>What can still be received on a PO line (no over-delivery).</summary>
    public static decimal StillDue(decimal ordered, decimal received) => Math.Max(0, ordered - received);

    /// <summary>What can still be invoiced on a PO line: received and not invoiced on invoices that count.</summary>
    public static decimal Invoiceable(decimal received, decimal invoiced) => Math.Max(0, received - invoiced);

    /// <summary>
    /// The 3-way match of one price: within <paramref name="tolerancePercent"/> of the PO price (0 = exactly). The
    /// quantity side is enforced before (never more than received), so it can't mismatch.
    /// </summary>
    public static bool PriceMatches(decimal orderPrice, decimal invoicePrice, decimal tolerancePercent) =>
        Math.Abs(invoicePrice - orderPrice) <= decimal.Round(orderPrice * tolerancePercent / 100m, 4);

    /// <summary>Quantity × price, rounded to the piastre the way supply records are.</summary>
    public static decimal Amount(decimal quantity, decimal unitPrice) =>
        decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);

    /// <summary>The committed cost of one PO line: ordered but not invoiced yet, at the PO price.</summary>
    public static decimal Committed(decimal ordered, decimal invoiced, decimal unitPrice) =>
        Amount(Math.Max(0, ordered - invoiced), unitPrice);
}

/// <summary>Reads how far each line has gone down the chain. Every figure is summed in SQL.</summary>
internal static class ChainQuantities
{
    public static async Task<Dictionary<int, decimal>> OrderedAsync(ProcurementDbContext db, IReadOnlyCollection<int> requisitionLineIds, CancellationToken cancellationToken) =>
        await db.PurchaseOrderLines.AsNoTracking()
            .Where(line => requisitionLineIds.Contains(line.RequisitionLineId))
            .Join(db.PurchaseOrders.Where(order => order.Status != PurchaseOrderStatus.Cancelled), line => line.OrderId, order => order.Id, (line, _) => line)
            .GroupBy(line => line.RequisitionLineId)
            .Select(group => new { group.Key, Quantity = group.Sum(line => line.Quantity) })
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Quantity, cancellationToken);

    public static async Task<Dictionary<int, decimal>> ReceivedAsync(ProcurementDbContext db, IReadOnlyCollection<int> orderLineIds, CancellationToken cancellationToken) =>
        await db.GoodsReceiptLines.AsNoTracking()
            .Where(line => orderLineIds.Contains(line.OrderLineId))
            .GroupBy(line => line.OrderLineId)
            .Select(group => new { group.Key, Quantity = group.Sum(line => line.Quantity) })
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Quantity, cancellationToken);

    public static async Task<Dictionary<int, decimal>> InvoicedAsync(ProcurementDbContext db, IReadOnlyCollection<int> orderLineIds, CancellationToken cancellationToken) =>
        await db.SupplierInvoiceLines.AsNoTracking()
            .Where(line => orderLineIds.Contains(line.OrderLineId))
            .Join(db.SupplierInvoices.Where(invoice => invoice.Status != SupplierInvoiceStatus.Returned), line => line.InvoiceId, invoice => invoice.Id, (line, _) => line)
            .GroupBy(line => line.OrderLineId)
            .Select(group => new { group.Key, Quantity = group.Sum(line => line.Quantity) })
            .ToDictionaryAsync(entry => entry.Key, entry => entry.Quantity, cancellationToken);
}
