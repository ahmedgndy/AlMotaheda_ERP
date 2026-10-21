using AlMotaheda.BuildingBlocks.Domain;
using AlMotaheda.Modules.Finance.Contracts;

namespace AlMotaheda.Modules.Finance.Data;

/// <summary>
/// One of the company's two treasury accounts (<see cref="LedgerAccount.Bank"/>, <see cref="LedgerAccount.Cash"/>):
/// its opening balance, set once by an Executive. The running balance is never stored: it is the opening balance
/// plus every ledger entry of that account from the opening date on (<c>TreasuryQueries</c>).
/// </summary>
internal sealed class TreasuryAccount
{
    public int Id { get; set; }

    public LedgerAccount Account { get; set; }

    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";

    /// <summary>What the account held on <see cref="OpeningDate"/>, before the system. Zero until set.</summary>
    public decimal OpeningBalance { get; set; }

    /// <summary>Entries dated before this day are history imported for the record and don't change the balance.</summary>
    public DateOnly OpeningDate { get; set; }

    /// <summary>Set once; a second attempt is refused (409 <c>opening_balance_set</c>). Corrections are ledger entries.</summary>
    public DateTime? OpeningSetAt { get; set; }
    public int? OpeningSetBy { get; set; }

    public string? Notes { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// A custody account of the hierarchy Executive → Accountant → Engineer → worker (or → vehicle, → payroll batch):
/// who answers for a sum of company cash and under whom. There is no balance column. For an account Finance ledgers
/// itself (<see cref="CustodyAccountKinds.IsLedgeredByFinance"/>) the balance is the sum of its posted
/// <see cref="CustodyMovement"/> rows; for an engineer's project custody or a vehicle it is read from the owning
/// module through <c>ICustodyLedgerSource</c>.
/// </summary>
internal sealed class CustodyAccount
{
    public int Id { get; set; }

    /// <summary><c>CA-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    /// <summary>One of <see cref="CustodyAccountKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The person who answers for the cash (an <c>access.User</c>). Empty for a vehicle: its driver is in Fleet.</summary>
    public int? HolderUserId { get; set; }

    /// <summary>What the account is for: the project (EngineerProject), the vehicle (Vehicle) or the batch (PayrollBatch).</summary>
    public int? ReferenceId { get; set; }

    /// <summary>The account the money normally comes from and goes back to (the accountant's for an engineer's).</summary>
    public int? ParentAccountId { get; set; }

    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";

    public CustodyAccountStatus Status { get; set; } = CustodyAccountStatus.Active;

    public string? Notes { get; set; }

    public DateTime OpenedAt { get; set; }
    public int OpenedBy { get; set; }

    public DateTime? ClosedAt { get; set; }
    public int? ClosedBy { get; set; }
    public string? CloseReason { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public bool IsLedgeredByFinance => CustodyAccountKinds.IsLedgeredByFinance(Kind);
}

/// <summary>Stored by name.</summary>
internal enum CustodyAccountStatus
{
    Active,
    Closed,
}

/// <summary>
/// One posted or pending movement of an account Finance ledgers (the accountant's custody, a payroll batch).
/// Append-only: never edited or deleted; a mistake is corrected by a reversal row pointing to the row it cancels.
/// <see cref="Amount"/> is signed: money into the custody is positive.
/// </summary>
/// <remarks>
/// Every row names its source (<see cref="SourceType"/>, <see cref="SourceId"/>), unique per account, so the same
/// transfer, payment or adjustment never moves the money twice, whatever retries or races happen.
/// </remarks>
internal sealed class CustodyMovement
{
    public long Id { get; set; }

    public int AccountId { get; set; }

    public DateOnly EntryDate { get; set; }

    public decimal Amount { get; set; }

    public CustodyMovementKind Kind { get; set; }

    /// <summary>Pending (an outflow waiting for its record's review), Posted, or Released (the record was rejected).</summary>
    public CustodyMovementStatus Status { get; set; } = CustodyMovementStatus.Posted;

    /// <summary>One of <see cref="CustodyMovementSources"/>.</summary>
    public string SourceType { get; set; } = "";
    public int SourceId { get; set; }

    public int? TransferId { get; set; }
    public int? PaymentRequestId { get; set; }

    /// <summary>Set on a reversal: the movement it cancels.</summary>
    public long? ReversesMovementId { get; set; }

    public string? Reason { get; set; }
    public string? Reference { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }
}

/// <summary>Stored by name.</summary>
internal enum CustodyMovementKind
{
    /// <summary>A received transfer: cash in.</summary>
    TransferIn,

    /// <summary>A disbursed transfer out: cash handed down the chain, or back up.</summary>
    TransferOut,

    /// <summary>A payment request paid out of this custody (a supplier, a worker, a salary sheet).</summary>
    Payment,

    /// <summary>A reviewed adjustment after a cash count, with its reason and evidence.</summary>
    Adjustment,

    /// <summary>The opposite of a posted movement, pointing to it.</summary>
    Reversal,
}

/// <summary>Stored by name.</summary>
internal enum CustodyMovementStatus
{
    Pending,
    Posted,
    Released,
}

/// <summary>The records a movement comes from (<see cref="CustodyMovement.SourceType"/>).</summary>
internal static class CustodyMovementSources
{
    public const string Transfer = CustodyLedgerSources.Transfer;
    public const string TransferReversal = CustodyLedgerSources.TransferReversal;
    public const string PaymentRequest = "PaymentRequest";
    public const string PaymentReversal = "PaymentRequestReversal";
    public const string Adjustment = CustodyLedgerSources.Adjustment;

    public static IReadOnlyList<string> All { get; } = [Transfer, TransferReversal, PaymentRequest, PaymentReversal, Adjustment];
}

/// <summary>Where one end of a transfer is: the treasury (Bank or Cash) or a custody account.</summary>
internal enum TransferEndpointKind
{
    Treasury,
    Custody,
}

/// <summary>
/// One internal transfer of cash: treasury → custody (an advance), custody → custody (a distribution down the chain)
/// or custody → treasury / parent (a return). Never an expense: the expense is recognised once, when the record paid
/// out of the custody is approved. Requested → Approved (never by the requester) → Disbursed (the source side is
/// posted) → Received (the destination side is posted); Rejected, Cancelled or Reversed otherwise.
/// </summary>
/// <remarks>
/// The amount and the two ends are fixed when the transfer is requested and never change. Balances move only on
/// disbursement and receipt. Every step is a <see cref="CustodyTransferEvent"/>.
/// </remarks>
internal sealed class CustodyTransfer
{
    public int Id { get; set; }

    /// <summary><c>CT-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    public TransferEndpointKind SourceKind { get; set; }
    public LedgerAccount? SourceTreasuryAccount { get; set; }
    public int? SourceAccountId { get; set; }

    public TransferEndpointKind DestinationKind { get; set; }
    public LedgerAccount? DestinationTreasuryAccount { get; set; }
    public int? DestinationAccountId { get; set; }

    public decimal Amount { get; set; }

    /// <summary><c>CustodyAdvance</c> (down the chain) or <c>CustodyReturn</c> (back up). See <see cref="PaymentPurposes"/>.</summary>
    public string Purpose { get; set; } = "";

    /// <summary>The payroll batch this transfer funds or distributes, when it is part of one.</summary>
    public int? PayrollBatchId { get; set; }

    /// <summary>The project of the engineer's custody at either end, so project people may see the transfer.</summary>
    public int? ProjectId { get; set; }

    public string? Reference { get; set; }
    public string? Notes { get; set; }

    /// <summary>The receipt or hand-over note: an Archive document id of the project when there is one, else a paper reference.</summary>
    public int? EvidenceDocumentId { get; set; }
    public string? EvidenceReference { get; set; }

    private CustodyTransferStatus _status = CustodyTransferStatus.Requested;

    public CustodyTransferStatus Status { get => _status; init => _status = value; }

    public DateTime RequestedAt { get; set; }
    public int RequestedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }

    public DateTime? DisbursedAt { get; set; }
    public int? DisbursedBy { get; set; }
    public DateOnly? DisbursedOn { get; set; }
    public string? DisbursementReference { get; set; }

    public DateTime? ReceivedAt { get; set; }
    public int? ReceivedBy { get; set; }
    public DateOnly? ReceivedOn { get; set; }

    public DateTime? ClosedAt { get; set; }
    public int? ClosedBy { get; set; }
    public string? CloseReason { get; set; }

    /// <summary>Made by the client for each form. The same key twice returns the same transfer.</summary>
    public Guid IdempotencyKey { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<CustodyTransferEvent> Events { get; } = [];

    private static readonly StatusFlow<CustodyTransferStatus> Flow = new(
        (CustodyTransferStatus.Requested, CustodyTransferStatus.Approved),
        (CustodyTransferStatus.Requested, CustodyTransferStatus.Rejected),
        (CustodyTransferStatus.Requested, CustodyTransferStatus.Cancelled),
        (CustodyTransferStatus.Approved, CustodyTransferStatus.Disbursed),
        (CustodyTransferStatus.Approved, CustodyTransferStatus.Cancelled),
        (CustodyTransferStatus.Disbursed, CustodyTransferStatus.Received),
        (CustodyTransferStatus.Disbursed, CustodyTransferStatus.Reversed),
        (CustodyTransferStatus.Received, CustodyTransferStatus.Reversed));

    public bool CanMoveTo(CustodyTransferStatus target) => Flow.Allows(_status, target);

    /// <summary>Open: the amount is reserved on the source until it is disbursed, rejected or cancelled.</summary>
    public bool ReservesSource => _status is CustodyTransferStatus.Requested or CustodyTransferStatus.Approved;

    /// <summary>Disbursed but not received: the cash is on its way to the destination.</summary>
    public bool InTransit => _status == CustodyTransferStatus.Disbursed;

    public void Approve(int userId, DateTime now, string? note)
    {
        Move(CustodyTransferStatus.Approved, "Approve", userId, now, note);
        DecidedAt = now;
        DecidedBy = userId;
        DecisionNote = note;
    }

    public void Reject(int userId, DateTime now, string note)
    {
        Move(CustodyTransferStatus.Rejected, "Reject", userId, now, note);
        DecidedAt = now;
        DecidedBy = userId;
        DecisionNote = note;
        Close(userId, now, note);
    }

    public void Disburse(int userId, DateTime now, DateOnly on, string? reference)
    {
        Move(CustodyTransferStatus.Disbursed, "Disburse", userId, now, reference);
        DisbursedAt = now;
        DisbursedBy = userId;
        DisbursedOn = on;
        DisbursementReference = reference;
    }

    public void Receive(int userId, DateTime now, DateOnly on, string? note)
    {
        Move(CustodyTransferStatus.Received, "Receive", userId, now, note);
        ReceivedAt = now;
        ReceivedBy = userId;
        ReceivedOn = on;
    }

    public void Cancel(int userId, DateTime now, string reason)
    {
        Move(CustodyTransferStatus.Cancelled, "Cancel", userId, now, reason);
        Close(userId, now, reason);
    }

    public void Reverse(int userId, DateTime now, string reason)
    {
        Move(CustodyTransferStatus.Reversed, "Reverse", userId, now, reason);
        Close(userId, now, reason);
    }

    private void Move(CustodyTransferStatus target, string action, int userId, DateTime now, string? note)
    {
        var from = _status;
        _status = Flow.Move(_status, target);
        Events.Add(new CustodyTransferEvent { Action = action, FromStatus = from, ToStatus = target, ActorUserId = userId, At = now, Note = note });
    }

    private void Close(int userId, DateTime now, string reason)
    {
        ClosedAt = now;
        ClosedBy = userId;
        CloseReason = reason;
    }
}

/// <summary>Stored by name.</summary>
internal enum CustodyTransferStatus
{
    Requested,
    Approved,
    Disbursed,
    Received,
    Rejected,
    Cancelled,
    Reversed,
}

/// <summary>One step of a transfer's history: who, when, from which status to which, and why.</summary>
internal sealed class CustodyTransferEvent
{
    public long Id { get; set; }

    public int TransferId { get; set; }

    public string Action { get; set; } = "";

    public CustodyTransferStatus? FromStatus { get; set; }
    public CustodyTransferStatus ToStatus { get; set; }

    public int ActorUserId { get; set; }

    public DateTime At { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// One alert to one person about a transfer (the Executives who approve it; the holder of its destination when it
/// is disbursed). Saved with the transfer; the Platform outbox turns it into a notification after the commit.
/// </summary>
internal sealed class CustodyTransferAlert
{
    public int Id { get; set; }

    public int TransferId { get; set; }
    public CustodyTransfer Transfer { get; set; } = null!;

    public int RecipientUserId { get; set; }

    /// <summary><c>Requested</c> (waiting for a decision) or <c>Disbursed</c> (waiting to be received).</summary>
    public string Stage { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A named payroll funding: the money the Executive sets aside for one period's wages (daily labor of some projects,
/// or a month's salary sheet), held in its own custody account by the accountant, distributed to the engineers'
/// project custodies and reconciled: funded, distributed, paid, remaining, returned.
/// </summary>
/// <remarks>
/// It never calculates pay: what is owed comes from Workforce or HR through <c>IPayrollEntitlementSource</c>, and a
/// distribution above the allocation of a project, or above what is still owed on it, is refused (409 <c>over_distribution</c>).
/// </remarks>
internal sealed class PayrollBatch
{
    public int Id { get; set; }

    /// <summary><c>PB-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    /// <summary>One of <see cref="PayrollBatchKinds"/>.</summary>
    public string Kind { get; set; } = "";

    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }

    /// <summary>For a salaries batch: the HR salary sheet.</summary>
    public int? SalarySheetId { get; set; }

    /// <summary>The batch's own custody account (kind PayrollBatch), held by the accountant.</summary>
    public int AccountId { get; set; }

    public int HolderUserId { get; set; }

    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";

    public PayrollBatchStatus Status { get; set; } = PayrollBatchStatus.Open;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int? ClosedBy { get; set; }
    public string? CloseNote { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<PayrollBatchAllocation> Allocations { get; } = [];
}

/// <summary>Stored by name.</summary>
internal enum PayrollBatchStatus
{
    Open,
    Closed,
}

/// <summary>What one project may receive from a daily-labor batch: at most what its approved, unpaid days add up to.</summary>
internal sealed class PayrollBatchAllocation
{
    public int Id { get; set; }

    public int BatchId { get; set; }

    public int ProjectId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>The entitlement the allocation was checked against when the batch was made (approved − paid).</summary>
    public decimal EntitlementAtAllocation { get; set; }
}

/// <summary>
/// A physical cash count of a custody against its recorded balance at that moment. Insert-only. A difference never
/// changes the balance: that takes a reviewed <see cref="CustodyAdjustment"/> with a reason and evidence.
/// </summary>
internal sealed class CustodyCashCount
{
    public int Id { get; set; }

    public int AccountId { get; set; }

    public DateOnly CountDate { get; set; }

    public decimal CountedAmount { get; set; }

    /// <summary>The posted balance when the count was recorded.</summary>
    public decimal BookBalance { get; set; }

    /// <summary>Counted − book: negative when cash is missing.</summary>
    public decimal Difference { get; set; }

    public string? Notes { get; set; }

    /// <summary>The photo of the count: an Archive document id or a paper reference.</summary>
    public string? EvidenceReference { get; set; }

    public Guid IdempotencyKey { get; set; }

    public DateTime CountedAt { get; set; }
    public int CountedBy { get; set; }
}

/// <summary>
/// A reviewed correction of a custody balance (after a cash count, a found error), with a mandatory reason and
/// evidence, by a reviewer who is not the holder. Insert-only: the movement it posts is never edited.
/// </summary>
internal sealed class CustodyAdjustment
{
    public int Id { get; set; }

    public int AccountId { get; set; }

    public int? CashCountId { get; set; }

    public DateOnly EntryDate { get; set; }

    /// <summary>Signed: positive adds to the custody, negative takes from it.</summary>
    public decimal Amount { get; set; }

    public string Reason { get; set; } = "";

    public string EvidenceReference { get; set; } = "";

    public Guid IdempotencyKey { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
}

/// <summary>
/// The close of a custody's period (a month): the balance at the period's last day, kept as a snapshot. Every
/// entry stays; after the close nothing may be dated inside the period (409 <c>period_closed</c>). The balance
/// carries forward by the policy <c>Finance:CustodyPeriodCarryForward</c>.
/// </summary>
internal sealed class CustodyPeriodClose
{
    public int Id { get; set; }

    public int AccountId { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public decimal ClosingBalance { get; set; }

    public decimal PendingAtClose { get; set; }

    public string? Notes { get; set; }

    public DateTime ClosedAt { get; set; }
    public int ClosedBy { get; set; }
}
