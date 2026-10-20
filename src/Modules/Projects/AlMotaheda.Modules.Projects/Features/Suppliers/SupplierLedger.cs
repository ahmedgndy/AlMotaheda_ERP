using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Suppliers;

/// <summary>One supply or record of a supplier, as every supplier figure sees it.</summary>
/// <param name="Amount">The adjusted amount (approved corrections included).</param>
/// <param name="Allocated">Payment allocations that are not voided (credit records only).</param>
internal sealed record SupplierRecordRow(
    int ExpenseId,
    int ProjectId,
    int SupplierId,
    int? SupplyAgreementId,
    int? ProjectItemId,
    ExpenseKind Kind,
    ExpenseStatus Status,
    PaymentChannel Channel,
    DateOnly Date,
    string? ItemName,
    string? Unit,
    decimal? Quantity,
    decimal Amount,
    decimal Allocated,
    string? InvoiceNo,
    int CreatedBy,
    int? ReviewedBy)
{
    public ExpenseSettlementRow Settlement => new(ExpenseId, Kind, Status, Channel, null, SupplierId, ProjectItemId, Amount, Allocated);

    /// <summary>Not approved yet and not rejected: delivered or spent, waiting for review.</summary>
    public bool IsPending => Status is ExpenseStatus.Recorded or ExpenseStatus.Returned;
}

/// <summary>One payment to a supplier, voided ones included.</summary>
internal sealed record SupplierPaymentRow(
    int Id, int ProjectId, int SupplierId, DateOnly PaymentDate, decimal Amount, PaymentMethod Method, string? Reference, int CreatedBy,
    DateTime? VoidedAt, int? VoidedBy, string? VoidReason);

/// <summary>
/// A supplier's figures on a project, a work item or across projects. Four things are never mixed:
/// <c>Committed</c> (ordered or agreed, not invoiced: a commitment), <c>Recognized</c> (approved records: the cost),
/// <c>Paid</c> and <c>Outstanding</c> (approved credit not paid yet: what is owed now), and <c>PendingReview</c>
/// (recorded, not reviewed: neither cost nor debt yet).
/// </summary>
/// <param name="CreditRecognized">Approved records bought on credit: the invoiced liability.</param>
/// <param name="CustodyRecognized">Approved records paid in cash from a custody (paid when bought).</param>
/// <param name="Committed">Purchase order lines ordered and not invoiced, at the order price.</param>
internal sealed record SupplierFigures(
    decimal Recognized,
    decimal CreditRecognized,
    decimal CustodyRecognized,
    decimal Paid,
    decimal Outstanding,
    decimal PendingReview,
    int PendingCount,
    int OpenInvoices,
    decimal Committed)
{
    public static SupplierFigures Of(IReadOnlyCollection<SupplierRecordRow> records, IReadOnlyCollection<SupplierOrderLine> orders)
    {
        var approved = records.Where(record => record.Status == ExpenseStatus.Approved).ToList();
        var credit = approved.Where(record => record.Channel == PaymentChannel.Credit).Sum(record => record.Amount);
        var custody = approved.Where(record => record.Channel == PaymentChannel.Custody).Sum(record => record.Amount);
        var paid = approved.Sum(record => ExpenseSettlement.Paid(record.Settlement));
        var pending = records.Where(record => record.IsPending).ToList();
        return new SupplierFigures(
            credit + custody,
            credit,
            custody,
            paid,
            credit + custody - paid,
            pending.Sum(record => record.Amount),
            pending.Count,
            approved.Count(record => record.Channel == PaymentChannel.Credit && ExpenseSettlement.Outstanding(record.Settlement) > 0),
            orders.Sum(line => line.Committed));
    }
}

/// <summary>What has been delivered under one supply agreement.</summary>
/// <param name="DeliveredQuantity">Approved supply quantity under the agreement.</param>
/// <param name="PendingQuantity">Supply recorded under it, waiting for review.</param>
/// <param name="RemainingQuantity">Agreed − delivered, never below zero; <c>null</c> when no quantity was agreed.</param>
/// <param name="RemainingValue">Agreed value − recognized, never below zero; <c>null</c> when no value was agreed.</param>
internal sealed record AgreementProgress(
    decimal DeliveredQuantity, decimal PendingQuantity, decimal? RemainingQuantity, decimal Recognized, decimal Invoiced, decimal Paid, decimal Outstanding,
    decimal? RemainingValue)
{
    public static AgreementProgress Of(SupplyAgreement agreement, IReadOnlyCollection<SupplierRecordRow> records)
    {
        var approved = records.Where(record => record.Status == ExpenseStatus.Approved).ToList();
        var delivered = approved.Sum(record => record.Quantity ?? 0);
        var recognized = approved.Sum(record => record.Amount);
        var paid = approved.Sum(record => ExpenseSettlement.Paid(record.Settlement));
        var agreedValue = agreement.AgreedValue ?? (agreement.AgreedQuantity * agreement.AgreedUnitPrice);
        return new AgreementProgress(
            delivered,
            records.Where(record => record.IsPending).Sum(record => record.Quantity ?? 0),
            agreement.AgreedQuantity is { } quantity ? Math.Max(quantity - delivered, 0) : null,
            recognized,
            approved.Where(record => record.Channel == PaymentChannel.Credit).Sum(record => record.Amount),
            paid,
            recognized - paid,
            agreedValue is { } value ? Math.Max(value - recognized, 0) : null);
    }
}

/// <summary>One kind of line on a supplier's ledger. Sent by name.</summary>
internal static class SupplierLedgerTypes
{
    /// <summary>A purchase order line: a commitment, never in the balance.</summary>
    public const string Order = "Order";

    /// <summary>An approved credit record: the company owes it (debit).</summary>
    public const string Invoice = "Invoice";

    /// <summary>An approved record paid in cash on site from a custody: owed and paid at once (no balance change).</summary>
    public const string CustodyPurchase = "CustodyPurchase";

    /// <summary>Money paid to the supplier (credit).</summary>
    public const string Payment = "Payment";

    /// <summary>A payment voided: the money no longer counts (debit back).</summary>
    public const string PaymentVoided = "PaymentVoided";
}

/// <summary>A dated fact of a supplier's account, before it becomes a ledger line.</summary>
internal sealed record SupplierLedgerFact(
    DateOnly Date, int Order, string Type, int ProjectId, int? ProjectItemId, string? Description, decimal? Quantity, string? Unit,
    decimal? Commitment, decimal Debit, decimal Credit, string SourceType, int SourceId, int? RecordedBy);

/// <summary>The supplier ledger's arithmetic and queries. The running balance is approved credit − payments.</summary>
internal static class SupplierLedger
{
    public static IReadOnlyList<(SupplierLedgerFact Fact, decimal Balance)> Run(IEnumerable<SupplierLedgerFact> facts)
    {
        decimal balance = 0;
        var lines = new List<(SupplierLedgerFact, decimal)>();
        foreach (var fact in facts.OrderBy(fact => fact.Date).ThenBy(fact => fact.Order).ThenBy(fact => fact.SourceId))
        {
            balance += fact.Debit - fact.Credit;
            lines.Add((fact, balance));
        }

        return lines;
    }

    /// <summary>The facts of the records, payments and order lines given (approved records only; pending ones are not owed).</summary>
    public static IEnumerable<SupplierLedgerFact> Facts(
        IEnumerable<SupplierRecordRow> records, IEnumerable<SupplierPaymentRow> payments, IEnumerable<SupplierOrderLine> orders)
    {
        foreach (var line in orders.Where(line => line.Status != "Cancelled"))
        {
            yield return new SupplierLedgerFact(
                line.OrderedOn, 0, SupplierLedgerTypes.Order, line.ProjectId, line.ProjectItemId, $"{line.OrderNumber} · {line.Description}",
                line.OrderedQuantity, line.Unit, line.OrderedValue, 0, 0, "PurchaseOrderLine", line.OrderLineId, null);
        }

        foreach (var record in records.Where(record => record.Status == ExpenseStatus.Approved))
        {
            var custody = record.Channel == PaymentChannel.Custody;
            yield return new SupplierLedgerFact(
                record.Date, 1, custody ? SupplierLedgerTypes.CustodyPurchase : SupplierLedgerTypes.Invoice, record.ProjectId, record.ProjectItemId,
                record.InvoiceNo is null ? record.ItemName : $"{record.ItemName} · {record.InvoiceNo}", record.Quantity, record.Unit, null,
                record.Amount, custody ? record.Amount : 0, nameof(ProjectExpense), record.ExpenseId, record.CreatedBy);
        }

        foreach (var payment in payments)
        {
            yield return new SupplierLedgerFact(
                payment.PaymentDate, 2, SupplierLedgerTypes.Payment, payment.ProjectId, null,
                payment.Reference is null ? payment.Method.ToString() : $"{payment.Method} · {payment.Reference}", null, null, null, 0, payment.Amount,
                nameof(SupplierPayment), payment.Id, payment.CreatedBy);
            if (payment.VoidedAt is { } voidedAt)
            {
                yield return new SupplierLedgerFact(
                    DateOnly.FromDateTime(voidedAt), 3, SupplierLedgerTypes.PaymentVoided, payment.ProjectId, null, payment.VoidReason, null, null, null,
                    payment.Amount, 0, nameof(SupplierPayment), payment.Id, payment.VoidedBy);
            }
        }
    }

    /// <summary>
    /// The supplier records of one project, or of one supplier across projects, or under some agreements (rejected
    /// ones left out). Filtered before the projection, so the filters run in SQL.
    /// </summary>
    public static IQueryable<SupplierRecordRow> Records(ProjectsDbContext db, int? projectId, int? supplierId, IReadOnlyCollection<int>? agreementIds = null) =>
        db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.SupplierId != null && expense.Status != ExpenseStatus.Rejected)
            .Where(expense => projectId == null || expense.ProjectId == projectId)
            .Where(expense => supplierId == null || expense.SupplierId == supplierId)
            .Where(expense => agreementIds == null || (expense.SupplyAgreementId != null && agreementIds.Contains(expense.SupplyAgreementId.Value)))
            .Select(expense => new SupplierRecordRow(
                expense.Id,
                expense.ProjectId,
                expense.SupplierId!.Value,
                expense.SupplyAgreementId,
                expense.ProjectItemId,
                expense.Kind,
                expense.Status,
                expense.Channel,
                expense.ExpenseDate,
                expense.Kind == ExpenseKind.Supply ? expense.ItemName : expense.Description,
                expense.Unit,
                expense.Quantity,
                expense.Amount + (expense.Adjustments.Where(adjustment => adjustment.Status == AdjustmentStatus.Approved)
                    .Sum(adjustment => (decimal?)adjustment.Amount) ?? 0),
                db.SupplierPaymentAllocations
                    .Where(allocation => allocation.ExpenseId == expense.Id && allocation.Payment.VoidedAt == null)
                    .Sum(allocation => allocation.Amount),
                expense.InvoiceNo,
                expense.CreatedBy,
                expense.ReviewedBy));

    public static IQueryable<SupplierPaymentRow> Payments(ProjectsDbContext db, int? projectId, int? supplierId) =>
        db.SupplierPayments.AsNoTracking()
            .Where(payment => projectId == null || payment.ProjectId == projectId)
            .Where(payment => supplierId == null || payment.SupplierId == supplierId)
            .Select(payment => new SupplierPaymentRow(
                payment.Id, payment.ProjectId, payment.SupplierId, payment.PaymentDate, payment.Amount, payment.Method, payment.Reference,
                payment.CreatedBy, payment.VoidedAt, payment.VoidedBy, payment.VoidReason));
}
