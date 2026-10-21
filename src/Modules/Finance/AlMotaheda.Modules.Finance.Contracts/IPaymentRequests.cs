using AlMotaheda.BuildingBlocks.Data;

namespace AlMotaheda.Modules.Finance.Contracts;

/// <summary>Asked by the source modules (Projects, Workforce, Subcontractors, Fleet). Implemented by Finance.</summary>
public interface IPaymentRequests
{
    /// <summary>The request made with this client key, or <c>null</c>. A retried form gets the same request back.</summary>
    Task<PaymentRequestRef?> FindByKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// How much open requests (not yet paid, not rejected or cancelled) plan to pay on each record, so a new request
    /// can't take the same money twice. Read inside the caller's transaction, after its lock.
    /// </summary>
    Task<IReadOnlyDictionary<int, decimal>> ReservedAsync(
        string recordType, IReadOnlyCollection<int> recordIds, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// How much open requests plan to pay each payee, for sources whose requests have no lines (a salary sheet is
    /// paid as one batch, its payee being the sheet). Read inside the caller's transaction, after its lock.
    /// </summary>
    Task<IReadOnlyDictionary<int, decimal>> HeldForPayeesAsync(
        string sourceType, string payeeType, IReadOnlyCollection<int> payeeIds, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>Stores a new request, status Submitted, in the caller's transaction.</summary>
    Task<PaymentRequestRef> SubmitAsync(NewPaymentRequest request, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// May <paramref name="payerUserId"/> pay a request out of custody account <paramref name="accountId"/>? The account
    /// must exist, be active, be one Finance ledgers (the accountant's custody, a payroll batch) and be held by the
    /// payer. Asked by a source module before it submits a request with <see cref="NewPaymentRequest.SourceCustodyAccountId"/>.
    /// </summary>
    Task<CustodySourceCheck> CheckCustodySourceAsync(int accountId, int payerUserId, CancellationToken cancellationToken);

    /// <summary>
    /// A paid payment was voided in its source module: writes the reversing ledger entries and marks the request
    /// Reversed, in the caller's transaction. Payments recorded before Finance existed were imported, so every
    /// voided payment has a request.
    /// </summary>
    Task ReverseAsync(string sourceType, int sourcePaymentId, string reason, int actorUserId, SharedTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>The answer to <see cref="IPaymentRequests.CheckCustodySourceAsync"/>.</summary>
public enum CustodySourceCheck
{
    Ok,

    /// <summary>No such account, or it is closed (400 on the field).</summary>
    NotFound,

    /// <summary>The payer doesn't hold it (403).</summary>
    NotHolder,

    /// <summary>Its ledger is kept by another module (an engineer's project custody pays through its own records).</summary>
    NotLedgered,
}

/// <summary>
/// A request as the source module answers its client (the body of the payment endpoints), and with enough detail to
/// tell a retried form (same key, same content) from a different payment that reuses a key.
/// </summary>
public sealed record PaymentRequestRef(
    int Id,
    string Number,
    string Status,
    string SourceType,
    string PayeeType,
    int PayeeId,
    int? ProjectId,
    string Purpose,
    decimal Amount,
    string Method,
    DateOnly RequestedDate,
    Guid IdempotencyKey,
    IReadOnlyList<PaymentLine> Lines,
    int? SourceCustodyAccountId = null);

/// <param name="ProjectId">The project, or <c>null</c> when the money spans projects (a worker paid for days on several).</param>
/// <param name="Lines">What the money pays for, each with its project. An advance has none.</param>
/// <param name="Payload">Source-specific details the source needs to write its payment later (JSON), for example the subcontract payment purpose.</param>
/// <param name="SourceCustodyAccountId">
/// The custody account the cash comes from (<c>finance.CustodyAccount</c>, a kind Finance ledgers: the accountant's
/// custody or a payroll batch), when the payer pays out of money already drawn from the treasury. Only with method
/// Cash; the request's creator must hold the account. The treasury is then unchanged when the request is paid: the
/// ledger entry is tagged <c>Custody</c> and the custody gets the outflow, once.
/// </param>
public sealed record NewPaymentRequest(
    string SourceType,
    string PayeeType,
    int PayeeId,
    int? ProjectId,
    string Purpose,
    decimal Amount,
    string Method,
    DateOnly RequestedDate,
    string? Reference,
    string? Notes,
    Guid IdempotencyKey,
    int CreatedBy,
    IReadOnlyList<PaymentLine> Lines,
    string? Payload = null,
    int? SourceCustodyAccountId = null);

/// <param name="RecordType">One of <see cref="PaidRecordTypes"/>.</param>
public sealed record PaymentLine(string RecordType, int RecordId, int ProjectId, decimal Amount);
