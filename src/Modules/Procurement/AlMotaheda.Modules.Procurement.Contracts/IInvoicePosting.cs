using AlMotaheda.BuildingBlocks.Data;

namespace AlMotaheda.Modules.Procurement.Contracts;

/// <summary>
/// Turns a matched supplier invoice into the project's supplier cost: one approved supply record on credit per
/// invoice line, on its cost item. Implemented by Projects, which owns those records, the supplier balances and the
/// supplier payments. Payment then follows the existing supplier payment request flow.
/// </summary>
public interface IInvoicePosting
{
    /// <summary>
    /// Writes the records in the caller's transaction and returns the record id of each invoice line. Throws
    /// <see cref="InvoiceNotPostableException"/> when a reference is no longer valid, and never posts a line twice.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> PostAsync(PostedInvoice invoice, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>What became of posted invoice lines: their record, its amount (corrections included) and what is paid.</summary>
    Task<IReadOnlyDictionary<int, PostedLineState>> StatesAsync(IReadOnlyCollection<int> invoiceLineIds, CancellationToken cancellationToken);
}

/// <param name="Reference">The documents, for the record's notes, for example <c>INV-000004 / PO-000002</c>.</param>
/// <param name="ApprovedBy">The Accountant who matched or resolved the invoice; the records' reviewer.</param>
public sealed record PostedInvoice(
    int ProjectId,
    int SupplierId,
    string InvoiceNo,
    DateOnly InvoiceDate,
    string? DeliveryNoteNo,
    string Reference,
    int ApprovedBy,
    IReadOnlyList<PostedInvoiceLine> Lines);

/// <param name="Unit">One of the shared measurement units.</param>
public sealed record PostedInvoiceLine(
    int InvoiceLineId, int? CatalogItemId, int? ProjectItemId, string ItemName, string Unit, decimal Quantity, decimal UnitPrice);

/// <param name="RecordId">The project's supply record.</param>
public sealed record PostedLineState(int InvoiceLineId, int RecordId, int ProjectId, decimal Amount, decimal Paid);

/// <summary>Projects refused to post: a supplier, catalog item or cost item is no longer valid.</summary>
public sealed class InvoiceNotPostableException(string message) : Exception(message);

/// <summary>The <c>SourceType</c> a posted record carries in Projects.</summary>
public static class PostedRecordSources
{
    public const string SupplierInvoiceLine = "SupplierInvoiceLine";
}
