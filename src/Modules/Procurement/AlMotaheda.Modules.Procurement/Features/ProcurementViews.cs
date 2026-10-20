using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Procurement.Common;
using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Features;

internal sealed record OrderingSupplier(int Id, string NameAr, string NameEn);

internal sealed record RequisitionView(
    int Id, string Number, int ProjectId, string Status, DateOnly? NeededBy, string? Notes,
    DateTime CreatedAt, ProcurementPersonRef? CreatedBy, DateTime? ApprovedAt, decimal? EstimatedTotal,
    IReadOnlyList<RequisitionLineView> Lines, IReadOnlyList<RequisitionEventView> History, int? QuotationRequestId, string RowVersion);

internal sealed record RequisitionLineView(
    int Id, int? CatalogItemId, int? ProjectItemId, string Description, string Unit, decimal Quantity, decimal Ordered, decimal StillNeeded,
    decimal? EstimatedUnitPrice, decimal? EstimatedAmount, int? PriceItemId = null);

/// <param name="Actor">The person; <c>null</c> for a system step, which has <c>actorLabel</c> and <c>policyName</c>.</param>
internal sealed record RequisitionEventView(string Action, string Status, ProcurementPersonRef? Actor, string? ActorLabel, string? PolicyName, DateTime At, string? Note);

internal sealed record QuotationRequestView(
    int Id, string Number, int RequisitionId, string RequisitionNumber, int ProjectId, string Status, int? QuotationsWanted, string? RequestNote,
    ProcurementPersonRef? RequestedBy, DateTime RequestedAt, int? RecommendedQuotationId, string? RecommendationReason,
    ProcurementPersonRef? SubmittedBy, DateTime? SubmittedAt, ProcurementPersonRef? DecidedBy, DateTime? DecidedAt, string? DecisionNote,
    int? SelectedQuotationId, int? CheapestQuotationId, IReadOnlyList<QuotationView> Quotations, string RowVersion);

internal sealed record QuotationView(
    int Id, OrderingSupplier Supplier, string? SupplierReference, int? DeliveryDays, string? PaymentTerms, DateOnly? ValidUntil, string? Notes,
    decimal? Total, IReadOnlyList<QuotationLineView> Lines);

internal sealed record QuotationLineView(int RequisitionLineId, string Description, string Unit, decimal Quantity, decimal? UnitPrice, decimal? Amount);

internal sealed record OrderView(
    int Id, string Number, int RequisitionId, string RequisitionNumber, int ProjectId, OrderingSupplier Supplier, int? QuotationId, string Status,
    int Revision, DateOnly? ExpectedDelivery, string? PaymentTerms, string? Notes, decimal? Total, decimal? Committed,
    DateTime CreatedAt, ProcurementPersonRef? CreatedBy, DateTime? CancelledAt, string? CancelReason,
    IReadOnlyList<OrderLineView> Lines, IReadOnlyList<OrderRevisionView> Revisions, string RowVersion);

internal sealed record OrderLineView(
    int Id, int RequisitionLineId, int? CatalogItemId, int? ProjectItemId, string Description, string Unit, decimal Quantity, decimal? UnitPrice,
    decimal? Amount, decimal Received, decimal Invoiced, decimal StillDue, int? PriceItemId = null);

/// <param name="PreviousLines">The lines before the revision (JSON); prices are left out without See money.</param>
internal sealed record OrderRevisionView(int RevisionNo, string Reason, DateTime CreatedAt, ProcurementPersonRef? CreatedBy, string? PreviousLines);

internal sealed record ReceiptView(
    int Id, string Number, int OrderId, string OrderNumber, int ProjectId, DateOnly ReceivedOn, string? DeliveryNoteNo, string? Notes,
    DateTime CreatedAt, ProcurementPersonRef? CreatedBy, IReadOnlyList<ReceiptLineView> Lines);

internal sealed record ReceiptLineView(int OrderLineId, string Description, string Unit, decimal Quantity);

/// <param name="PaymentState"><c>Unpaid</c>, <c>PartlyPaid</c> or <c>Paid</c> once posted; <c>null</c> before.</param>
internal sealed record InvoiceView(
    int Id, string Number, int OrderId, string OrderNumber, int ProjectId, OrderingSupplier Supplier, string InvoiceNo, DateOnly InvoiceDate,
    string? DeliveryNoteNo, string Status, decimal? Total, string? Resolution, string? ResolutionReason, string? CreditNoteNo,
    ProcurementPersonRef? ResolvedBy, DateTime? ResolvedAt, DateTime? PostedAt, DateTime CreatedAt, ProcurementPersonRef? CreatedBy,
    decimal? Paid, string? PaymentState, IReadOnlyList<InvoiceLineView> Lines, string RowVersion);

/// <param name="PostedRecordId">The project's supply record this line became (pay it through the supplier payments).</param>
/// <param name="PriceCheck">Price Control's comparison on the invoice date (WithinRange, AboveRange…), when the order line names a price item.</param>
/// <param name="PriceExceptionId">The case the Executive decides when the price was above its range.</param>
internal sealed record InvoiceLineView(
    int Id, int OrderLineId, string Description, string Unit, decimal Ordered, decimal Received, decimal Quantity,
    decimal? OrderUnitPrice, decimal? UnitPrice, bool PriceMatches, decimal? Amount, int? PostedRecordId, decimal? Paid,
    int? PriceItemId = null, string? PriceCheck = null, int? PriceExceptionId = null);

/// <summary>Builds the views. Reads names from Access and Projects through their contracts.</summary>
internal sealed class ProcurementViews(ProcurementDbContext db, IUserDirectory users, ISupplierDirectory suppliers, IInvoicePosting posting)
{
    public async Task<RequisitionView?> RequisitionAsync(int id, bool seesMoney, CancellationToken cancellationToken)
    {
        var requisition = await db.Requisitions.AsNoTracking()
            .Include(candidate => candidate.Lines)
            .Include(candidate => candidate.Events)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (requisition is null)
        {
            return null;
        }

        var ordered = await ChainQuantities.OrderedAsync(db, requisition.Lines.Select(line => line.Id).ToList(), cancellationToken);
        var requestId = await db.QuotationRequests.AsNoTracking()
            .Where(request => request.RequisitionId == id).Select(request => (int?)request.Id).SingleOrDefaultAsync(cancellationToken);
        var people = await users.FindAsync(
            requisition.Events.Where(entry => entry.ActorUserId is not null).Select(entry => entry.ActorUserId!.Value).Append(requisition.CreatedBy).Distinct().ToList(),
            cancellationToken);

        var lines = requisition.Lines.OrderBy(line => line.Id).Select(line =>
        {
            var done = ordered.GetValueOrDefault(line.Id);
            var estimate = line.EstimatedUnitPrice is { } price ? ProcurementRules.Amount(line.Quantity, price) : (decimal?)null;
            return new RequisitionLineView(line.Id, line.CatalogItemId, line.ProjectItemId, line.Description, line.Unit, line.Quantity, done,
                ProcurementRules.StillNeeded(line.Quantity, done), seesMoney ? line.EstimatedUnitPrice : null, seesMoney ? estimate : null, line.PriceItemId);
        }).ToList();

        return new RequisitionView(
            requisition.Id, requisition.Number, requisition.ProjectId, requisition.Status.ToString(), requisition.NeededBy, requisition.Notes,
            requisition.CreatedAt, ProcurementPersonRef.From(requisition.CreatedBy, people), requisition.ApprovedAt,
            seesMoney ? lines.Sum(line => line.EstimatedAmount ?? 0) : null,
            lines,
            requisition.Events.OrderBy(entry => entry.Id).Select(entry => new RequisitionEventView(
                entry.Action, entry.Status.ToString(), ProcurementPersonRef.From(entry.ActorUserId, people), entry.ActorLabel, entry.PolicyName, entry.At, entry.Note)).ToList(),
            requestId,
            RowVersions.Format(requisition.RowVersion));
    }

    public async Task<QuotationRequestView?> QuotationRequestAsync(int id, bool seesMoney, CancellationToken cancellationToken)
    {
        var request = await db.QuotationRequests.AsNoTracking()
            .Include(candidate => candidate.Quotations).ThenInclude(quotation => quotation.Lines)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (request is null)
        {
            return null;
        }

        var requisition = await db.Requisitions.AsNoTracking().Include(candidate => candidate.Lines)
            .SingleAsync(candidate => candidate.Id == request.RequisitionId, cancellationToken);
        var prLines = requisition.Lines.ToDictionary(line => line.Id);
        var names = await suppliers.FindAsync(request.Quotations.Select(quotation => quotation.SupplierId).Distinct().ToList(), cancellationToken);
        var people = await users.FindAsync(
            new[] { request.RequestedBy, request.SubmittedBy ?? 0, request.DecidedBy ?? 0 }.Where(user => user > 0).Distinct().ToList(), cancellationToken);
        var cheapest = request.Quotations.OrderBy(quotation => quotation.Total).ThenBy(quotation => quotation.Id).FirstOrDefault()?.Id;

        return new QuotationRequestView(
            request.Id, request.Number, request.RequisitionId, requisition.Number, request.ProjectId, request.Status.ToString(), request.QuotationsWanted,
            request.RequestNote, ProcurementPersonRef.From(request.RequestedBy, people), request.RequestedAt, request.RecommendedQuotationId,
            request.RecommendationReason, ProcurementPersonRef.From(request.SubmittedBy, people), request.SubmittedAt,
            ProcurementPersonRef.From(request.DecidedBy, people), request.DecidedAt, request.DecisionNote, request.SelectedQuotationId,
            seesMoney ? cheapest : null,
            request.Quotations.OrderBy(quotation => quotation.Id).Select(quotation => new QuotationView(
                quotation.Id, Supplier(quotation.SupplierId, names), quotation.SupplierReference, quotation.DeliveryDays, quotation.PaymentTerms,
                quotation.ValidUntil, quotation.Notes, seesMoney ? quotation.Total : null,
                quotation.Lines.OrderBy(line => line.RequisitionLineId).Select(line =>
                {
                    var prLine = prLines[line.RequisitionLineId];
                    return new QuotationLineView(line.RequisitionLineId, prLine.Description, prLine.Unit, prLine.Quantity,
                        seesMoney ? line.UnitPrice : null, seesMoney ? ProcurementRules.Amount(prLine.Quantity, line.UnitPrice) : null);
                }).ToList())).ToList(),
            RowVersions.Format(request.RowVersion));
    }

    public async Task<OrderView?> OrderAsync(int id, bool seesMoney, CancellationToken cancellationToken)
    {
        var order = await db.PurchaseOrders.AsNoTracking()
            .Include(candidate => candidate.Lines)
            .Include(candidate => candidate.Revisions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var lineIds = order.Lines.Select(line => line.Id).ToList();
        var received = await ChainQuantities.ReceivedAsync(db, lineIds, cancellationToken);
        var invoiced = await ChainQuantities.InvoicedAsync(db, lineIds, cancellationToken);
        var requisitionNumber = await db.Requisitions.AsNoTracking().Where(requisition => requisition.Id == order.RequisitionId)
            .Select(requisition => requisition.Number).SingleAsync(cancellationToken);
        var names = await suppliers.FindAsync([order.SupplierId], cancellationToken);
        var people = await users.FindAsync(order.Revisions.Select(revision => revision.CreatedBy).Append(order.CreatedBy).Distinct().ToList(), cancellationToken);

        var lines = order.Lines.OrderBy(line => line.Id).Select(line =>
        {
            var got = received.GetValueOrDefault(line.Id);
            return new OrderLineView(line.Id, line.RequisitionLineId, line.CatalogItemId, line.ProjectItemId, line.Description, line.Unit, line.Quantity,
                seesMoney ? line.UnitPrice : null, seesMoney ? ProcurementRules.Amount(line.Quantity, line.UnitPrice) : null,
                got, invoiced.GetValueOrDefault(line.Id), ProcurementRules.StillDue(line.Quantity, got), line.PriceItemId);
        }).ToList();
        var committed = order.Status == PurchaseOrderStatus.Cancelled
            ? 0
            : order.Lines.Sum(line => ProcurementRules.Committed(line.Quantity, invoiced.GetValueOrDefault(line.Id), line.UnitPrice));

        return new OrderView(
            order.Id, order.Number, order.RequisitionId, requisitionNumber, order.ProjectId, Supplier(order.SupplierId, names), order.QuotationId,
            order.Status.ToString(), order.Revision, order.ExpectedDelivery, order.PaymentTerms, order.Notes,
            seesMoney ? lines.Sum(line => line.Amount ?? 0) : null, seesMoney ? committed : null,
            order.CreatedAt, ProcurementPersonRef.From(order.CreatedBy, people), order.CancelledAt, order.CancelReason,
            lines,
            order.Revisions.OrderBy(revision => revision.RevisionNo).Select(revision => new OrderRevisionView(
                revision.RevisionNo, revision.Reason, revision.CreatedAt, ProcurementPersonRef.From(revision.CreatedBy, people),
                seesMoney ? revision.PreviousLines : null)).ToList(),
            RowVersions.Format(order.RowVersion));
    }

    public async Task<ReceiptView?> ReceiptAsync(int id, CancellationToken cancellationToken)
    {
        var receipt = await db.GoodsReceipts.AsNoTracking().Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (receipt is null)
        {
            return null;
        }

        var orderLines = await db.PurchaseOrderLines.AsNoTracking().Where(line => line.OrderId == receipt.OrderId).ToDictionaryAsync(line => line.Id, cancellationToken);
        var orderNumber = await db.PurchaseOrders.AsNoTracking().Where(order => order.Id == receipt.OrderId).Select(order => order.Number).SingleAsync(cancellationToken);
        var people = await users.FindAsync([receipt.CreatedBy], cancellationToken);

        return new ReceiptView(
            receipt.Id, receipt.Number, receipt.OrderId, orderNumber, receipt.ProjectId, receipt.ReceivedOn, receipt.DeliveryNoteNo, receipt.Notes,
            receipt.CreatedAt, ProcurementPersonRef.From(receipt.CreatedBy, people),
            receipt.Lines.OrderBy(line => line.Id).Select(line => new ReceiptLineView(
                line.OrderLineId, orderLines[line.OrderLineId].Description, orderLines[line.OrderLineId].Unit, line.Quantity)).ToList());
    }

    public async Task<InvoiceView?> InvoiceAsync(int id, bool seesMoney, CancellationToken cancellationToken)
    {
        var invoice = await db.SupplierInvoices.AsNoTracking().Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (invoice is null)
        {
            return null;
        }

        var orderLines = await db.PurchaseOrderLines.AsNoTracking().Where(line => line.OrderId == invoice.OrderId).ToDictionaryAsync(line => line.Id, cancellationToken);
        var received = await ChainQuantities.ReceivedAsync(db, orderLines.Keys.ToList(), cancellationToken);
        var orderNumber = await db.PurchaseOrders.AsNoTracking().Where(order => order.Id == invoice.OrderId).Select(order => order.Number).SingleAsync(cancellationToken);
        var names = await suppliers.FindAsync([invoice.SupplierId], cancellationToken);
        var people = await users.FindAsync(new[] { invoice.CreatedBy, invoice.ResolvedBy ?? 0 }.Where(user => user > 0).Distinct().ToList(), cancellationToken);
        var posted = invoice.Status == SupplierInvoiceStatus.Posted
            ? await posting.StatesAsync(invoice.Lines.Select(line => line.Id).ToList(), cancellationToken)
            : new Dictionary<int, PostedLineState>();

        decimal? paid = posted.Count > 0 ? posted.Values.Sum(state => state.Paid) : null;
        var postedAmount = posted.Values.Sum(state => state.Amount);
        string? paymentState = paid is null ? null : paid == 0 ? "Unpaid" : paid >= postedAmount ? "Paid" : "PartlyPaid";

        return new InvoiceView(
            invoice.Id, invoice.Number, invoice.OrderId, orderNumber, invoice.ProjectId, Supplier(invoice.SupplierId, names), invoice.InvoiceNo,
            invoice.InvoiceDate, invoice.DeliveryNoteNo, invoice.Status.ToString(), seesMoney ? invoice.Total : null, invoice.Resolution?.ToString(),
            invoice.ResolutionReason, invoice.CreditNoteNo, ProcurementPersonRef.From(invoice.ResolvedBy, people), invoice.ResolvedAt, invoice.PostedAt,
            invoice.CreatedAt, ProcurementPersonRef.From(invoice.CreatedBy, people), seesMoney ? paid : null, paymentState,
            invoice.Lines.OrderBy(line => line.Id).Select(line =>
            {
                var orderLine = orderLines[line.OrderLineId];
                var state = posted.GetValueOrDefault(line.Id);
                return new InvoiceLineView(line.Id, line.OrderLineId, orderLine.Description, orderLine.Unit, orderLine.Quantity,
                    received.GetValueOrDefault(line.OrderLineId), line.Quantity, seesMoney ? line.OrderUnitPrice : null, seesMoney ? line.UnitPrice : null,
                    line.PriceMatches, seesMoney ? ProcurementRules.Amount(line.Quantity, line.UnitPrice) : null, state?.RecordId,
                    seesMoney ? state?.Paid : null, orderLine.PriceItemId, line.PriceCheck, line.PriceExceptionId);
            }).ToList(),
            RowVersions.Format(invoice.RowVersion));
    }

    private static OrderingSupplier Supplier(int id, IReadOnlyDictionary<int, SupplierRef> names) =>
        names.TryGetValue(id, out var supplier) ? new OrderingSupplier(id, supplier.NameAr, supplier.NameEn) : new OrderingSupplier(id, $"#{id}", $"#{id}");
}
