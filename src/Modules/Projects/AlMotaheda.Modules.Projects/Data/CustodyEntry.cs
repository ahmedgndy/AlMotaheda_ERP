namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// One movement of a person's cash custody ("العهدة") on one project, other than the purchases recorded as project
/// expenses (those are read from <see cref="ProjectExpense"/> itself, channel Custody, by their recorder).
/// </summary>
/// <remarks>
/// <para><b>Funding</b>: cash handed to the holder. Written only when its Finance payment request is paid, so the
/// company's ledger has the money out and the custody has the money in, once. Not a project cost.</para>
/// <para><b>Return</b>: cash the holder handed back. Recorded by a custody approver.</para>
/// <para><b>Payroll</b>: wages the holder paid in cash out of the custody (a weekly labor settlement in Workforce).
/// Pending while the settlement waits for review (it already lowers the balance: the cash is gone), Posted when it
/// is confirmed, Released when it is rejected or reversed. Never deleted.</para>
/// <para>Balance = Σ posted fundings − Σ posted returns − Σ custody expenses not rejected − Σ payroll not released.</para>
/// </remarks>
internal sealed class CustodyEntry
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The person whose custody it is (an <c>access.User</c>).</summary>
    public int HolderUserId { get; set; }

    public CustodyEntryKind Kind { get; set; }

    /// <summary>Always positive; <see cref="Kind"/> says which way it moves.</summary>
    public decimal Amount { get; set; }

    public DateOnly EntryDate { get; set; }

    /// <summary>Cash, BankTransfer or Cheque (Finance's payment methods).</summary>
    public required string Method { get; set; }

    public string? Reference { get; set; }
    public string? Notes { get; set; }

    public CustodyEntryStatus Status { get; set; } = CustodyEntryStatus.Posted;

    /// <summary>The record behind the movement: <c>PaymentRequest</c> (a funding) or <c>PayrollSettlement</c> (wages paid). Unique with <see cref="SourceId"/>.</summary>
    public string? SourceType { get; set; }
    public int? SourceId { get; set; }

    /// <summary>A return's client key: sending the same form twice records it once.</summary>
    public Guid? IdempotencyKey { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

internal enum CustodyEntryKind
{
    Funding,
    Return,
    Payroll,

    /// <summary>A reviewed adjustment up (after a cash count), with its reason and evidence, by a custody reviewer.</summary>
    AdjustmentIn,

    /// <summary>A reviewed adjustment down.</summary>
    AdjustmentOut,
}

/// <summary>The sign of a custody entry kind: cash in is positive.</summary>
internal static class CustodyEntryKinds
{
    public static bool IsCashIn(CustodyEntryKind kind) => kind is CustodyEntryKind.Funding or CustodyEntryKind.AdjustmentIn;
}

internal enum CustodyEntryStatus
{
    /// <summary>Waiting for the paying record's review; already lowers the balance.</summary>
    Pending,
    Posted,

    /// <summary>The paying record was rejected or reversed: counts nowhere, kept for the audit.</summary>
    Released,
}
