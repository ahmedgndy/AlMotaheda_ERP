using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Procurement.Common;
using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Integration;

/// <summary>
/// Price Control for supplier invoices: each line whose order line names a price item is compared, on the invoice
/// date, with the supplier's range (or the general one). Above the range, under the gating policy, the invoice is
/// held (<see cref="SupplierInvoiceStatus.PriceHold"/>) and a case is opened for the Executive; nothing is posted,
/// so nothing can be paid, until the case is approved. Under the blocking policy the invoice is refused.
/// </summary>
internal sealed class InvoicePriceChecks(IPriceControl priceControl, ProcurementLog log)
{
    /// <summary>One line's verdict, with the order line it was compared for.</summary>
    internal sealed record LineVerdict(SupplierInvoiceLine Line, PurchaseOrderLine OrderLine, PriceVerdict Verdict);

    /// <summary>Compares every line that names a price item. Returns the refusal under the blocking policy, or the verdicts.</summary>
    public async Task<(IReadOnlyList<LineVerdict> Verdicts, ProblemHttpResult? Refused)> CheckAsync(
        SupplierInvoice invoice, IReadOnlyDictionary<int, PurchaseOrderLine> orderLines, bool priceFromOrder, CancellationToken cancellationToken)
    {
        var verdicts = new List<LineVerdict>();
        foreach (var line in invoice.Lines)
        {
            var orderLine = orderLines[line.OrderLineId];
            if (orderLine.PriceItemId is not { } priceItemId)
            {
                continue;
            }

            var unitPrice = priceFromOrder ? line.OrderUnitPrice : line.UnitPrice;
            var verdict = await priceControl.CheckAsync(
                new PriceCheckRequest(priceItemId, invoice.SupplierId, invoice.InvoiceDate, orderLine.Unit, unitPrice, line.Quantity), cancellationToken);
            line.PriceCheck = verdict.Result;
            if (verdict.IsAboveRange && verdict.Blocked)
            {
                return ([], ProcurementProblems.PriceBlocked(orderLine.Description, unitPrice, verdict.MaxPrice ?? 0));
            }

            verdicts.Add(new LineVerdict(line, orderLine, verdict));
        }

        return (verdicts, null);
    }

    /// <summary>The lines above their range that the policy holds for the Executive.</summary>
    public static IReadOnlyList<LineVerdict> Held(IReadOnlyList<LineVerdict> verdicts) =>
        verdicts.Where(verdict => verdict.Verdict.IsAboveRange && verdict.Verdict.Gates).ToList();

    /// <summary>
    /// Opens one case per held line (the lines have their ids: the invoice was saved), in the caller's transaction,
    /// and records the case on the line.
    /// </summary>
    public async Task OpenCasesAsync(
        SupplierInvoice invoice, IReadOnlyList<LineVerdict> held, bool priceFromOrder, string? reason, int userId, SharedTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var (line, orderLine, verdict) in held)
        {
            var unitPrice = priceFromOrder ? line.OrderUnitPrice : line.UnitPrice;
            line.PriceExceptionId = await priceControl.OpenExceptionAsync(
                new PriceExceptionDraft(
                    PriceExceptionSources.SupplierInvoiceLine, line.Id, invoice.ProjectId, invoice.SupplierId, orderLine.PriceItemId!.Value, verdict,
                    orderLine.Unit, line.Quantity, unitPrice, reason, $"{orderLine.Description} · INV {invoice.InvoiceNo}", invoice.InvoiceDate, userId),
                transaction,
                cancellationToken);
        }

        log.InvoiceHeldForPrice(invoice.Id, held.Count);
    }

    /// <summary>
    /// The refusal when any line of the invoice has a case that is not approved (pending or rejected), or <c>null</c>.
    /// Asked before an invoice is posted by any path, so no screen gets around the Executive.
    /// </summary>
    public async Task<ProblemHttpResult?> OpenCaseRefusalAsync(SupplierInvoice invoice, CancellationToken cancellationToken)
    {
        var lineIds = invoice.Lines.Where(line => line.PriceExceptionId != null).Select(line => line.Id).ToList();
        if (lineIds.Count == 0)
        {
            return null;
        }

        var states = await priceControl.StatesAsync(PriceExceptionSources.SupplierInvoiceLine, lineIds, cancellationToken);
        var open = states.Values.FirstOrDefault(state => state.IsOpen);
        return open is null ? null
            : open.Status == "Rejected" ? ProcurementProblems.PriceExceptionWasRejected(open.ExceptionId) : ProcurementProblems.PriceExceptionIsPending(open.ExceptionId);
    }
}

/// <summary>
/// Procurement's answer to the Executive's decision on an invoice line's price (<see cref="IPriceExceptionSource"/>),
/// in Projects' transaction: once every held line of the invoice is approved, the invoice is posted as the project's
/// supplier cost (or goes to the Accountant's mismatch queue when its prices also differ from the order); a rejected
/// price sends it to that queue, where only a return or a credit note at the order price can resolve it.
/// </summary>
internal sealed class InvoicePriceExceptionSource(
    DbContextOptions<ProcurementDbContext> options,
    IPriceControl priceControl,
    IInvoicePosting posting,
    CompanyClock clock,
    ProcurementLog log) : IPriceExceptionSource
{
    public string SourceType => PriceExceptionSources.SupplierInvoiceLine;

    public async Task OnDecidedAsync(PriceExceptionDecision decision, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await transaction.JoinAsync(options, ProcurementDatabase.Configure, joinedOptions => new ProcurementDbContext(joinedOptions), cancellationToken);

        var invoiceId = await joined.SupplierInvoiceLines.AsNoTracking()
            .Where(line => line.Id == decision.SourceId)
            .Select(line => (int?)line.InvoiceId)
            .SingleOrDefaultAsync(cancellationToken);
        if (invoiceId is null)
        {
            return;
        }

        var invoice = await joined.SupplierInvoices.Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoiceId, cancellationToken);
        if (invoice.Status != SupplierInvoiceStatus.PriceHold)
        {
            return;
        }

        if (!decision.Approved)
        {
            invoice.ReleaseToMismatch();
            await joined.SaveChangesAsync(cancellationToken);
            log.InvoicePriceDecided(decision.DecidedBy, decision.ExceptionId, decision.Status, invoice.Id, invoice.Status.ToString());
            return;
        }

        // The other held lines were decided in earlier transactions: approved means every one of them is approved.
        var others = invoice.Lines.Where(line => line.PriceExceptionId != null && line.Id != decision.SourceId).Select(line => line.Id).ToList();
        var states = await priceControl.StatesAsync(PriceExceptionSources.SupplierInvoiceLine, others, cancellationToken);
        if (others.Any(lineId => !states.TryGetValue(lineId, out var state) || !state.IsApproved))
        {
            return;
        }

        if (!invoice.Lines.All(line => line.PriceMatches))
        {
            // The price is accepted by the Executive, but it still differs from the order: the Accountant resolves that.
            invoice.ReleaseToMismatch();
            await joined.SaveChangesAsync(cancellationToken);
            log.InvoicePriceDecided(decision.DecidedBy, decision.ExceptionId, decision.Status, invoice.Id, invoice.Status.ToString());
            return;
        }

        var order = await joined.PurchaseOrders.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == invoice.OrderId, cancellationToken);
        await InvoicePosting.PostAsync(joined, posting, invoice, order, priceFromOrder: false, decision.DecidedBy, clock.UtcNow, transaction, cancellationToken);
        log.InvoicePriceDecided(decision.DecidedBy, decision.ExceptionId, decision.Status, invoice.Id, invoice.Status.ToString());
    }
}

/// <summary>Posts an invoice into the project's supplier cost (Projects, same transaction) and marks it Posted.</summary>
internal static class InvoicePosting
{
    /// <summary>With a credit note the lines are posted at the PO price: the supplier credits the rest. Throws <see cref="InvoiceNotPostableException"/>.</summary>
    public static async Task PostAsync(
        ProcurementDbContext db, IInvoicePosting posting, SupplierInvoice invoice, PurchaseOrder order, bool priceFromOrder, int userId, DateTime now,
        SharedTransaction transaction, CancellationToken cancellationToken)
    {
        var orderLines = order.Lines.ToDictionary(line => line.Id);
        await posting.PostAsync(
            new PostedInvoice(
                invoice.ProjectId,
                invoice.SupplierId,
                invoice.InvoiceNo,
                invoice.InvoiceDate,
                invoice.DeliveryNoteNo,
                $"{invoice.Number} / {order.Number}",
                userId,
                invoice.Lines.OrderBy(line => line.Id).Select(line => new PostedInvoiceLine(
                    line.Id,
                    orderLines[line.OrderLineId].CatalogItemId,
                    orderLines[line.OrderLineId].ProjectItemId,
                    orderLines[line.OrderLineId].Description,
                    orderLines[line.OrderLineId].Unit,
                    line.Quantity,
                    priceFromOrder ? line.OrderUnitPrice : line.UnitPrice,
                    orderLines[line.OrderLineId].PriceItemId)).ToList()),
            transaction,
            cancellationToken);

        invoice.MarkPosted(userId, now);
        await db.SaveChangesAsync(cancellationToken);
    }
}
