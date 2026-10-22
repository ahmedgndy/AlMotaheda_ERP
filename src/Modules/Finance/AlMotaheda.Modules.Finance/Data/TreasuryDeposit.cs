namespace AlMotaheda.Modules.Finance.Data;

/// <summary>
/// Money put into the company treasury (Bank or Cash) from outside the system: owner capital, a loan, a client's
/// payment. Recorded by an Executive, written with its one ledger entry in the same transaction, and never updated
/// or deleted: it is money in, not income, and a correction is a new entry.
/// </summary>
internal sealed class TreasuryDeposit
{
    public int Id { get; set; }

    /// <summary><c>TD-000123</c>, computed by the database from the id.</summary>
    public string Number { get; set; } = "";

    /// <summary><see cref="LedgerAccount.Bank"/> or <see cref="LedgerAccount.Cash"/>.</summary>
    public LedgerAccount Account { get; set; }

    public decimal Amount { get; set; }

    /// <summary>The day the money arrived: the ledger entry's date.</summary>
    public DateOnly DepositDate { get; set; }

    /// <summary>Where the money came from, as the Executive names it ("Owner capital", "Bank loan", a client).</summary>
    public string Source { get; set; } = "";

    /// <summary>The bank transfer or cheque number, or the receipt number for cash.</summary>
    public string? Reference { get; set; }

    public string? Notes { get; set; }

    public Guid IdempotencyKey { get; set; }

    public DateTime RecordedAt { get; set; }

    public int RecordedBy { get; set; }
}
