namespace AlMotaheda.Modules.Procurement.Contracts;

/// <summary>
/// The purchase order lines of a project or a supplier, with how far each has gone: ordered, received, invoiced.
/// Asked by Projects for the supplier screens; implemented by Procurement. Ordered is a commitment, received is a
/// physical quantity, invoiced is a liability: the three are returned apart and never added together.
/// </summary>
public interface ISupplierOrders
{
    /// <summary>The order lines of <paramref name="projectId"/> and/or <paramref name="supplierId"/> (at least one is given).</summary>
    Task<IReadOnlyList<SupplierOrderLine>> LinesAsync(int? projectId, int? supplierId, CancellationToken cancellationToken);
}

/// <param name="Committed">Ordered but not invoiced yet, at the order price: (ordered − invoiced) × unit price, never below zero; 0 for a cancelled order.</param>
/// <param name="Status">The order's status: Issued, PartiallyReceived, Received or Cancelled.</param>
/// <param name="OrderedOn">The day the order was issued.</param>
public sealed record SupplierOrderLine(
    int OrderId,
    string OrderNumber,
    int ProjectId,
    int SupplierId,
    int OrderLineId,
    int? ProjectItemId,
    string Description,
    string Unit,
    decimal OrderedQuantity,
    decimal UnitPrice,
    decimal OrderedValue,
    decimal ReceivedQuantity,
    decimal InvoicedQuantity,
    decimal Committed,
    string Status,
    DateOnly? ExpectedDelivery,
    DateOnly OrderedOn);
