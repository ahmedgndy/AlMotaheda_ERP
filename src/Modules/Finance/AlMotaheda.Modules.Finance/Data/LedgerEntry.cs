namespace AlMotaheda.Modules.Finance.Data;

/// <summary>
/// One movement of money, per project: the book every money report reconciles to. Written in the same transaction
/// as the payment it records, and never updated or deleted; a voided payment adds reversing entries.
/// </summary>
/// <remarks>Single entry with account tags (refactor plan, proposal 1). Money out is negative.</remarks>
internal sealed class LedgerEntry
{
    public long Id { get; set; }

    /// <summary>The business date the money moved (the payment's paid date).</summary>
    public DateOnly EntryDate { get; set; }

    public LedgerAccount Account { get; set; }

    /// <summary>Signed: money out of the company is negative.</summary>
    public decimal Amount { get; set; }

    /// <summary>One of <c>PayeeTypes</c>.</summary>
    public string CounterpartyType { get; set; } = "";

    public int CounterpartyId { get; set; }

    /// <summary><c>null</c> only for money not tied to a project: salaries (company overhead).</summary>
    public int? ProjectId { get; set; }

    /// <summary>One of <c>PaymentPurposes</c>.</summary>
    public string Purpose { get; set; } = "";

    /// <summary>The paid request, or <c>null</c> for an entry of a custody transfer (<see cref="CustodyTransferId"/>).</summary>
    public int? PaymentRequestId { get; set; }

    /// <summary>The internal transfer that moved the money, when it was not a payment request.</summary>
    public int? CustodyTransferId { get; set; }

    /// <summary>For <see cref="LedgerAccount.Custody"/>: the custody account the money left, instead of the treasury.</summary>
    public int? CustodyAccountId { get; set; }

    /// <summary>One of <c>PaymentSourceTypes</c>, and the payment row in that module.</summary>
    public string SourceType { get; set; } = "";
    public int? SourcePaymentId { get; set; }

    /// <summary>Set on a reversing entry: the entry it cancels.</summary>
    public long? ReversesEntryId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The user who confirmed the payment or voided it; <c>null</c> for imported history.</summary>
    public int? CreatedBy { get; set; }
}

/// <summary>Where the money came from. Stored by name.</summary>
internal enum LedgerAccount
{
    Bank,
    Cash,

    /// <summary>
    /// A custody account (<c>CustodyAccountId</c>): cash already drawn from the treasury, paid out by its holder. The
    /// treasury balances count only Bank and Cash; custody entries are the payments of the custody ledger.
    /// </summary>
    Custody,
}
