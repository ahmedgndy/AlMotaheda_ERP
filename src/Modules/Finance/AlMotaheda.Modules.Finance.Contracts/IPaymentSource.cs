using AlMotaheda.BuildingBlocks.Data;

namespace AlMotaheda.Modules.Finance.Contracts;

/// <summary>
/// Implemented by each module that owns payments (one per <see cref="PaymentSourceTypes"/> value). Asked by Finance
/// when a request becomes Paid: write the payment row exactly as planned, in Finance's transaction.
/// </summary>
public interface IPaymentSource
{
    string SourceType { get; }

    /// <summary>
    /// Writes the paid payment and returns its id. Throws <see cref="PaymentNotPayableException"/> rather than pay
    /// something that is no longer payable.
    /// </summary>
    Task<int> SettleAsync(PaymentSettlement settlement, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>The payees' names, for the request lists. Unknown ids are left out.</summary>
    Task<IReadOnlyDictionary<int, PayeeName>> PayeeNamesAsync(IReadOnlyCollection<int> payeeIds, CancellationToken cancellationToken);
}

public sealed record PayeeName(string NameAr, string NameEn);

/// <summary>The source module refused to settle: what the request pays for can no longer be paid as planned.</summary>
public sealed class PaymentNotPayableException(string message) : Exception(message);

/// <param name="PaidOn">The date the money left (the bank's value date, or the cash date).</param>
/// <param name="Reference">The bank's or the cash receipt's reference.</param>
/// <param name="ConfirmedBy">The user who confirmed the payment; the source payment's recorder.</param>
public sealed record PaymentSettlement(
    int RequestId,
    string RequestNumber,
    int PayeeId,
    int? ProjectId,
    decimal Amount,
    string Method,
    DateOnly PaidOn,
    string? Reference,
    string? Notes,
    Guid IdempotencyKey,
    int RequestedBy,
    int ConfirmedBy,
    IReadOnlyList<PaymentLine> Lines,
    string? Payload);
