using AlMotaheda.BuildingBlocks.Data;

namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// The company's price control ("ضبط الأسعار"), owned by Projects: one catalog of reference prices and allowed
/// ranges that every module compares its prices with before a transaction is finalized. Other modules (Procurement:
/// supplier invoices) check a price here and open a case when it is outside its range; the Executive decides the
/// case in the Price Control inbox and the module that owns the transaction is told (<see cref="IPriceExceptionSource"/>).
/// </summary>
public interface IPriceControl
{
    /// <summary>The price items with these ids (unknown ids are left out), for a module that lets a line name one.</summary>
    Task<IReadOnlyDictionary<int, PriceItemRef>> FindItemsAsync(IReadOnlyCollection<int> priceItemIds, CancellationToken cancellationToken);

    /// <summary>
    /// How a unit price compares with the range that applies on a date (the supplier's own range first). The unit must
    /// be the item's unit, otherwise the two can't be compared (<see cref="PriceVerdict.UnitMismatch"/>).
    /// </summary>
    Task<PriceVerdict> CheckAsync(PriceCheckRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a case for a transaction above its range, in the caller's transaction, with the alerts to the people who
    /// decide and reconcile it. Returns the case id. A second open case for the same transaction is refused by the database.
    /// </summary>
    Task<int> OpenExceptionAsync(PriceExceptionDraft draft, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>The newest case of each transaction (any status), by transaction id. Transactions with no case are left out.</summary>
    Task<IReadOnlyDictionary<int, PriceExceptionState>> StatesAsync(string sourceType, IReadOnlyCollection<int> sourceIds, CancellationToken cancellationToken);

    /// <summary>
    /// The active price items of these categories (by category code) with the general range that applies on
    /// <paramref name="date"/>, for a module that offers a short catalog of its own (the office expenses).
    /// </summary>
    Task<IReadOnlyList<PriceItemListing>> ListItemsAsync(IReadOnlyCollection<string> categoryCodes, DateOnly date, CancellationToken cancellationToken);

    /// <summary>
    /// The transaction was corrected by its submitter: every case still counting against it (pending or rejected) is
    /// withdrawn in the caller's transaction, so a corrected price starts clean. Returns how many were withdrawn.
    /// </summary>
    Task<int> WithdrawExceptionsAsync(string sourceType, int sourceId, int userId, SharedTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by a module whose transactions can carry a price exception (one per <see cref="PriceExceptionSources"/>
/// value other than the project record, which Projects handles itself). Called by Projects, in its transaction, when
/// the Executive decides a case on one of the module's transactions.
/// </summary>
public interface IPriceExceptionSource
{
    string SourceType { get; }

    Task OnDecidedAsync(PriceExceptionDecision decision, SharedTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>The kinds of transaction a price case can be about.</summary>
public static class PriceExceptionSources
{
    /// <summary>A supply or site expense of a project (Projects).</summary>
    public const string ProjectExpense = "ProjectExpense";

    /// <summary>A line of a supplier invoice (Procurement).</summary>
    public const string SupplierInvoiceLine = "SupplierInvoiceLine";

    /// <summary>An office expense paid from the company office's cash custody (Office). Company overhead: no project.</summary>
    public const string OfficeExpense = "OfficeExpense";

    public static IReadOnlyList<string> All { get; } = [ProjectExpense, SupplierInvoiceLine, OfficeExpense];
}

public sealed record PriceItemRef(int Id, string NameAr, string NameEn, string Unit, string CategoryCode, bool IsActive);

/// <summary>A price item with the general reference range in force on the asked date (all <c>null</c> when none applies).</summary>
public sealed record PriceItemListing(PriceItemRef Item, int? RangeId, decimal? MinPrice, decimal? MaxPrice, decimal? ReferencePrice);

/// <param name="Quantity">For the total variance; 1 for a lump sum.</param>
public sealed record PriceCheckRequest(int PriceItemId, int? SupplierId, DateOnly Date, string Unit, decimal UnitPrice, decimal Quantity);

/// <param name="Result">NoReference, WithinRange, AboveRange, BelowRange or UnitMismatch.</param>
/// <param name="Gates">True when, under the company policy, a transaction above its range must wait for the Executive's decision.</param>
/// <param name="Blocked">True when the company policy refuses a transaction above its range outright.</param>
public sealed record PriceVerdict(
    string Result,
    int? RangeId,
    decimal? MinPrice,
    decimal? MaxPrice,
    decimal? ReferencePrice,
    decimal VariancePerUnit,
    decimal VarianceTotal,
    decimal VariancePercent,
    bool Gates,
    bool Blocked)
{
    public const string NoReference = "NoReference";
    public const string WithinRange = "WithinRange";
    public const string AboveRange = "AboveRange";
    public const string BelowRange = "BelowRange";
    public const string UnitMismatch = "UnitMismatch";

    public bool IsAboveRange => Result == AboveRange;
}

/// <param name="ProjectId">The project, or <c>null</c> for company overhead (an office expense).</param>
/// <param name="Summary">What the transaction is, for the inbox (item, invoice number).</param>
public sealed record PriceExceptionDraft(
    string SourceType,
    int SourceId,
    int? ProjectId,
    int? SupplierId,
    int PriceItemId,
    PriceVerdict Verdict,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    string? Reason,
    string Summary,
    DateOnly TransactionDate,
    int SubmittedBy);

/// <param name="Status">Pending, ApprovedOnce, ApprovedWithPriceChange, Rejected or Withdrawn.</param>
public sealed record PriceExceptionState(int ExceptionId, int SourceId, string Status)
{
    public bool IsApproved => Status is "ApprovedOnce" or "ApprovedWithPriceChange";

    /// <summary>Pending or rejected: the transaction waits.</summary>
    public bool IsOpen => Status is "Pending" or "Rejected";
}

/// <param name="Approved">True for decision A or B; false for C.</param>
public sealed record PriceExceptionDecision(int ExceptionId, string SourceType, int SourceId, int? ProjectId, string Status, bool Approved, int DecidedBy, string Note);
