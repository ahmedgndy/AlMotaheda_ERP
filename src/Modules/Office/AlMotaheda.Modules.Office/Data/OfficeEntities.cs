using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Office.Data;

/// <summary>
/// One cash custody of the company office ("عهدة المكتب"), held by one person (the office custodian). There is no
/// balance column: the recorded balance is always the sum of its posted ledger entries (<see cref="OfficeLedgerEntry"/>).
/// </summary>
internal sealed class OfficeCustody
{
    public int Id { get; set; }

    public required string NameAr { get; set; }
    public required string NameEn { get; set; }

    /// <summary>The custodian: the only person who records expenses and confirms received funds on this custody.</summary>
    public int HolderUserId { get; set; }

    public OfficeCustodyStatus Status { get; set; } = OfficeCustodyStatus.Active;

    /// <summary>Below this recorded balance the custodian and finance are warned (0 = no warning).</summary>
    public decimal LowBalanceThreshold { get; set; }

    /// <summary>When the last low-balance warning went out; cleared when the balance is back above the threshold.</summary>
    public DateTime? LowBalanceWarnedAt { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    /// <summary>Who suspended, reopened or closed it last, when and why.</summary>
    public DateTime? StatusChangedAt { get; set; }
    public int? StatusChangedBy { get; set; }
    public string? StatusReason { get; set; }

    public byte[] RowVersion { get; set; } = [];

    /// <summary>New expenses, funding and counts are accepted only on an active custody.</summary>
    public bool IsActive => Status == OfficeCustodyStatus.Active;
}

internal enum OfficeCustodyStatus
{
    Active,

    /// <summary>Temporarily stopped (an investigation, a custodian on leave). History stays; nothing new is accepted.</summary>
    Suspended,

    /// <summary>Closed for good with a zero balance. History stays readable.</summary>
    Closed,
}

/// <summary>
/// One posted movement of a custody's cash. Append-only: a row is never edited or deleted; a mistake is corrected by a
/// new row (a reversal or an adjustment) that points to what it corrects. <see cref="Amount"/> is signed: money into
/// the custody is positive, money out is negative.
/// </summary>
/// <remarks>
/// Every row has a unique source (<see cref="SourceType"/>, <see cref="SourceId"/>), so the same expense, funding,
/// count or return can never be posted twice, whatever retries or races happen.
/// </remarks>
internal sealed class OfficeLedgerEntry
{
    public long Id { get; set; }

    public int CustodyId { get; set; }

    public OfficeLedgerKind Kind { get; set; }

    /// <summary>Signed: + into the custody (funding, reversal of an expense), − out of it (expense, return).</summary>
    public decimal Amount { get; set; }

    /// <summary>The business date (the expense's date, the day the funds were received).</summary>
    public DateOnly EntryDate { get; set; }

    /// <summary>One of <see cref="OfficeLedgerSources"/>: the record this movement comes from.</summary>
    public required string SourceType { get; set; }

    public int SourceId { get; set; }

    /// <summary>The entry this one corrects (a reversal of a posted expense).</summary>
    public long? ReversesEntryId { get; set; }

    public string? Reason { get; set; }
    public string? Reference { get; set; }

    public DateTime PostedAt { get; set; }
    public int PostedBy { get; set; }
}

internal enum OfficeLedgerKind
{
    /// <summary>Funds the custodian confirmed receiving (from a paid funding request).</summary>
    Funding,

    /// <summary>An approved office expense paid from the custody.</summary>
    Expense,

    /// <summary>Cash handed back to the company.</summary>
    Return,

    /// <summary>A posted expense reversed (the money is back in the recorded balance).</summary>
    Reversal,

    /// <summary>A reviewed cash-count difference posted with its reason.</summary>
    Adjustment,
}

/// <summary>The records a ledger entry can come from (unique per record).</summary>
internal static class OfficeLedgerSources
{
    public const string FundingRequest = "OfficeFundingRequest";
    public const string Expense = "OfficeExpense";
    public const string ExpenseReversal = "OfficeExpenseReversal";
    public const string CashReturn = "OfficeCashReturn";
    public const string CashCount = "OfficeCashCount";

    public static IReadOnlyList<string> All { get; } = [FundingRequest, Expense, ExpenseReversal, CashReturn, CashCount];
}

/// <summary>
/// Cash handed back to the company by the custodian, recorded by finance (never by the custodian). Its own row so a
/// retried form is stored once (idempotency key) and the ledger entry has a source.
/// </summary>
internal sealed class OfficeCashReturn
{
    public int Id { get; set; }
    public int CustodyId { get; set; }
    public decimal Amount { get; set; }
    public DateOnly ReturnedOn { get; set; }
    public required string Reason { get; set; }
    public string? Reference { get; set; }
    public Guid IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
}

/// <summary>
/// "طلب تغذية العهدة": the custodian (or finance, for the opening funding) asks for cash. Approving it only creates a
/// Finance payment request; the custody balance changes only when the custodian confirms receiving the money after
/// Finance paid it. Requested, approved, disbursed and received amounts are kept apart.
/// </summary>
internal sealed class OfficeFundingRequest
{
    public int Id { get; set; }
    public int CustodyId { get; set; }

    public decimal RequestedAmount { get; set; }
    public required string Reason { get; set; }
    public DateOnly RequestedDate { get; set; }
    public Guid IdempotencyKey { get; set; }

    public int RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }

    private OfficeFundingStatus _status = OfficeFundingStatus.Submitted;

    public OfficeFundingStatus Status { get => _status; init => _status = value; }

    /// <summary>The amount the reviewer agreed (at most the requested amount); what Finance is asked to pay.</summary>
    public decimal? ApprovedAmount { get; set; }
    public int? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    /// <summary>The Finance payment request made on approval, and its client key (to read its live status).</summary>
    public int? PaymentRequestId { get; set; }
    public string? PaymentRequestNumber { get; set; }
    public Guid? PaymentRequestKey { get; set; }
    public string? PaymentMethod { get; set; }

    /// <summary>What Finance paid out, when and with which reference (set when Finance marks its request paid).</summary>
    public decimal? DisbursedAmount { get; set; }
    public DateOnly? DisbursedOn { get; set; }
    public string? DisbursementReference { get; set; }
    public int? DisbursedBy { get; set; }

    /// <summary>What the custodian confirmed receiving; posted to the ledger once, as a funding entry.</summary>
    public decimal? ReceivedAmount { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public int? ReceivedBy { get; set; }
    public string? ReceiptNote { get; set; }

    public DateTime? CancelledAt { get; set; }
    public int? CancelledBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<OfficeFundingStatus> Flow = new(
        (OfficeFundingStatus.Submitted, OfficeFundingStatus.Approved),
        (OfficeFundingStatus.Submitted, OfficeFundingStatus.Rejected),
        (OfficeFundingStatus.Submitted, OfficeFundingStatus.Cancelled),
        (OfficeFundingStatus.Approved, OfficeFundingStatus.Disbursed),
        (OfficeFundingStatus.Approved, OfficeFundingStatus.Cancelled),
        (OfficeFundingStatus.Disbursed, OfficeFundingStatus.Received));

    public bool CanMoveTo(OfficeFundingStatus target) => Flow.Allows(_status, target);

    public void MoveTo(OfficeFundingStatus target) => _status = Flow.Move(_status, target);
}

internal enum OfficeFundingStatus
{
    Submitted,

    /// <summary>Agreed by finance; a Finance payment request waits for the Executive and the payment.</summary>
    Approved,

    Rejected,

    /// <summary>Finance paid it out; the custodian must confirm receiving it.</summary>
    Disbursed,

    /// <summary>The custodian confirmed the cash; the funding is in the ledger.</summary>
    Received,

    Cancelled,
}

/// <summary>
/// An office expense voucher: money the custodian already paid from the custody, with camera-captured receipts. It is
/// reviewed (<see cref="Status"/>) and, only once fully approved, posted to the ledger (<see cref="PostingStatus"/>).
/// A submitted or approved expense is never shown as deducted until it is posted.
/// </summary>
internal sealed class OfficeExpense
{
    public int Id { get; set; }
    public int CustodyId { get; set; }

    /// <summary>One of <see cref="OfficeExpenseCategories"/>.</summary>
    public required string Category { get; set; }

    /// <summary>The central Price Control item this expense is compared with, when it names one.</summary>
    public int? PriceItemId { get; set; }

    public required string Description { get; set; }
    public string? Purpose { get; set; }
    public string? Vendor { get; set; }
    public string? InvoiceNo { get; set; }

    public decimal Quantity { get; set; }
    public required string Unit { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }

    /// <summary>round(quantity × unit price, 2) − discount + tax, computed by the server.</summary>
    public decimal Amount { get; set; }

    public DateOnly ExpenseDate { get; set; }
    public Guid IdempotencyKey { get; set; }

    /// <summary>The repair or purchase request this expense completes (one cost, one deduction).</summary>
    public int? SpendRequestId { get; set; }

    // The price comparison as it was when the expense was (re)submitted: kept as is when the catalog changes later.
    public string? PriceCheck { get; set; }
    public int? PriceRangeId { get; set; }
    public decimal? ReferencePrice { get; set; }
    public decimal? ReferenceMinPrice { get; set; }
    public decimal? ReferenceMaxPrice { get; set; }
    public decimal? VariancePerUnit { get; set; }
    public decimal? VarianceTotal { get; set; }
    public decimal? VariancePercent { get; set; }
    public string? PriceJustification { get; set; }

    /// <summary>The central Price Control case opened for a price above its range (decided by the Executive there).</summary>
    public int? PriceExceptionId { get; set; }

    /// <summary>1 for a routine expense, 2 when the policy asks for a second approver (one of them an Executive).</summary>
    public int RequiredApprovals { get; set; } = 1;

    /// <summary>Why the expense needs more attention, comma separated (see <see cref="OfficeReviewReasons"/>).</summary>
    public string? ReviewReasons { get; set; }

    /// <summary>Other expenses that look like the same purchase (receipt hash, invoice number, vendor and amount).</summary>
    public string? DuplicateOf { get; set; }

    /// <summary>Increases on every resubmission after a return; only approvals of the current round count.</summary>
    public int Round { get; set; } = 1;

    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime SubmittedAt { get; set; }

    private OfficeExpenseStatus _status = OfficeExpenseStatus.Submitted;

    public OfficeExpenseStatus Status { get => _status; init => _status = value; }

    public int? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    public OfficePostingStatus PostingStatus { get; set; } = OfficePostingStatus.NotPosted;
    public long? LedgerEntryId { get; set; }
    public DateTime? PostedAt { get; set; }

    public long? ReversalEntryId { get; set; }
    public int? ReversedBy { get; set; }
    public DateTime? ReversedAt { get; set; }
    public string? ReversalReason { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<OfficeExpenseStatus> Flow = new(
        (OfficeExpenseStatus.Submitted, OfficeExpenseStatus.PartlyApproved),
        (OfficeExpenseStatus.Submitted, OfficeExpenseStatus.Approved),
        (OfficeExpenseStatus.Submitted, OfficeExpenseStatus.Returned),
        (OfficeExpenseStatus.Submitted, OfficeExpenseStatus.Rejected),
        (OfficeExpenseStatus.PartlyApproved, OfficeExpenseStatus.Approved),
        (OfficeExpenseStatus.PartlyApproved, OfficeExpenseStatus.Returned),
        (OfficeExpenseStatus.PartlyApproved, OfficeExpenseStatus.Rejected),
        (OfficeExpenseStatus.Returned, OfficeExpenseStatus.Submitted),
        // An approved expense whose price case the Executive rejected can still be rejected (it never posted).
        (OfficeExpenseStatus.Approved, OfficeExpenseStatus.Rejected));

    public bool CanMoveTo(OfficeExpenseStatus target) => Flow.Allows(_status, target);

    public void MoveTo(OfficeExpenseStatus target) => _status = Flow.Move(_status, target);

    /// <summary>Waiting for a reviewer: submitted, or approved by one of two required approvers.</summary>
    public bool InReview => _status is OfficeExpenseStatus.Submitted or OfficeExpenseStatus.PartlyApproved;

    /// <summary>Counts against the available balance: submitted or approved, but not posted and not rejected.</summary>
    public bool IsPendingCommitment =>
        PostingStatus == OfficePostingStatus.NotPosted && _status is not (OfficeExpenseStatus.Rejected or OfficeExpenseStatus.Returned);
}

internal enum OfficeExpenseStatus
{
    Submitted,

    /// <summary>One of two required approvals is in.</summary>
    PartlyApproved,

    /// <summary>Every required approval is in. Posted at once, unless a price case still waits for the Executive.</summary>
    Approved,

    /// <summary>Sent back to the custodian to correct and resubmit.</summary>
    Returned,

    /// <summary>Final: not an office expense of the company. Never posted.</summary>
    Rejected,
}

internal enum OfficePostingStatus
{
    NotPosted,
    Posted,

    /// <summary>Posted, then reversed by a linked entry; both stay in the ledger.</summary>
    Reversed,
}

/// <summary>One approval of an expense by one reviewer in one review round.</summary>
internal sealed class OfficeExpenseApproval
{
    public int Id { get; set; }
    public int ExpenseId { get; set; }
    public int Round { get; set; }
    public int ApproverUserId { get; set; }

    /// <summary>The approver's role when approving (an Executive's approval satisfies the second-approval rule).</summary>
    public required string ApproverRole { get; set; }

    public string? Note { get; set; }
    public DateTime ApprovedAt { get; set; }
}

/// <summary>The office expense categories. Stored by name.</summary>
internal static class OfficeExpenseCategories
{
    public const string Internet = "Internet";
    public const string Stationery = "Stationery";
    public const string Cleaning = "Cleaning";
    public const string Refreshments = "Refreshments";
    public const string Utilities = "Utilities";
    public const string Equipment = "Equipment";
    public const string Maintenance = "Maintenance";
    public const string Transport = "Transport";
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } = [Internet, Stationery, Cleaning, Refreshments, Utilities, Equipment, Maintenance, Transport, Other];
}

/// <summary>Why an expense needs more than a routine review.</summary>
internal static class OfficeReviewReasons
{
    /// <summary>The unit price is above the central reference range.</summary>
    public const string AboveRange = "AboveRange";

    /// <summary>The unit can't be compared with the price item's unit.</summary>
    public const string UnitMismatch = "UnitMismatch";

    /// <summary>Above the routine approval limit.</summary>
    public const string AboveLimit = "AboveLimit";

    /// <summary>Looks like another expense (same receipt, invoice, or vendor, amount and date).</summary>
    public const string DuplicateRisk = "DuplicateRisk";

    /// <summary>A repair (always reviewed twice).</summary>
    public const string Repair = "Repair";

    /// <summary>An emergency repair recorded after the fact.</summary>
    public const string Emergency = "Emergency";

    /// <summary>The final cost is above the approved estimate of its repair or purchase request.</summary>
    public const string AboveEstimate = "AboveEstimate";

    /// <summary>More than the custody's available balance.</summary>
    public const string AboveAvailable = "AboveAvailable";

    /// <summary>No reference price exists for the item on that date (information only).</summary>
    public const string NoReference = "NoReference";
}

/// <summary>
/// A repair or a purchase asked before the money is spent (a proposed expenditure), with its evidence: before-repair
/// photos and a quotation, then after-repair photos and the final invoice. Completing it creates the one office
/// expense that carries its cost; the request itself never moves the ledger.
/// </summary>
internal sealed class OfficeSpendRequest
{
    public int Id { get; set; }
    public int CustodyId { get; set; }

    public OfficeSpendKind Kind { get; set; }

    public required string Title { get; set; }
    public required string Description { get; set; }
    public string? Location { get; set; }
    public string? Asset { get; set; }
    public string? Supplier { get; set; }

    /// <summary>The expected cost or quotation, when known.</summary>
    public decimal? EstimatedAmount { get; set; }

    /// <summary>Work already done before asking (post-facto): a reason is required and the final expense gets elevated review.</summary>
    public bool IsEmergency { get; set; }
    public string? EmergencyReason { get; set; }

    public Guid IdempotencyKey { get; set; }
    public int RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; }

    private OfficeSpendStatus _status = OfficeSpendStatus.Submitted;

    public OfficeSpendStatus Status { get => _status; init => _status = value; }

    public int? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    /// <summary>A reviewer's authorized exception: completion may go on without after-repair photos, with this reason.</summary>
    public string? AfterEvidenceWaivedReason { get; set; }
    public int? AfterEvidenceWaivedBy { get; set; }
    public DateTime? AfterEvidenceWaivedAt { get; set; }

    /// <summary>The office expense that carries the final cost.</summary>
    public int? ExpenseId { get; set; }
    public DateTime? CompletedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<OfficeSpendStatus> Flow = new(
        (OfficeSpendStatus.Submitted, OfficeSpendStatus.Approved),
        (OfficeSpendStatus.Submitted, OfficeSpendStatus.Rejected),
        (OfficeSpendStatus.Submitted, OfficeSpendStatus.Cancelled),
        (OfficeSpendStatus.Approved, OfficeSpendStatus.Completed),
        (OfficeSpendStatus.Approved, OfficeSpendStatus.Cancelled),
        // An emergency repair is already done: its final expense may be recorded before the request is reviewed.
        (OfficeSpendStatus.Submitted, OfficeSpendStatus.Completed),
        // Its expense was rejected: the request waits for a corrected expense again.
        (OfficeSpendStatus.Completed, OfficeSpendStatus.Approved));

    public bool CanMoveTo(OfficeSpendStatus target) => Flow.Allows(_status, target);

    public void MoveTo(OfficeSpendStatus target) => _status = Flow.Move(_status, target);
}

internal enum OfficeSpendKind
{
    Repair,
    Purchase,
}

internal enum OfficeSpendStatus
{
    Submitted,
    Approved,
    Rejected,

    /// <summary>The final expense was recorded (it is reviewed and posted on its own).</summary>
    Completed,

    Cancelled,
}

/// <summary>
/// A file attached to an office record: a receipt, before/after repair photos, a quotation, a transfer slip, a cash
/// count photo. Uploaded first (unbound), then bound to exactly one record when that record is saved. A bound file is
/// never re-bound, replaced or deleted.
/// </summary>
internal sealed class OfficeAttachment
{
    public int Id { get; set; }

    public OfficeAttachmentPurpose Purpose { get; set; }

    /// <summary>One of <see cref="OfficeAttachmentOwners"/>; <c>null</c> while not yet bound.</summary>
    public string? OwnerType { get; set; }
    public int? OwnerId { get; set; }

    public required string StorageKey { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the stored bytes (lower-case hex), computed by the server; used to spot duplicate receipts.</summary>
    public required string Sha256 { get; set; }

    /// <summary>How the client says the image was made ("Camera"); the server cannot prove it, see the module README.</summary>
    public required string CaptureSource { get; set; }

    /// <summary>When the phone took it (client clock), and the phone's own quality reading of the image.</summary>
    public DateTime? CapturedAt { get; set; }
    public string? DeviceModel { get; set; }
    public string? QualityNote { get; set; }

    public Guid IdempotencyKey { get; set; }
    public int UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
    public DateTime? BoundAt { get; set; }
}

internal enum OfficeAttachmentPurpose
{
    Receipt,
    BeforeRepair,
    AfterRepair,
    Quotation,
    Transfer,
    CashCount,
    Supporting,
}

/// <summary>The records an attachment can belong to.</summary>
internal static class OfficeAttachmentOwners
{
    public const string Expense = "OfficeExpense";
    public const string FundingRequest = "OfficeFundingRequest";
    public const string SpendRequest = "OfficeSpendRequest";
    public const string CashCount = "OfficeCashCount";

    public static IReadOnlyList<string> All { get; } = [Expense, FundingRequest, SpendRequest, CashCount];
}

/// <summary>
/// A cash count (reconciliation): the cash physically counted against the recorded balance at that moment. A
/// difference never changes the balance by itself: a reviewer either accepts the explanation or posts an adjustment.
/// </summary>
internal sealed class OfficeCashCount
{
    public int Id { get; set; }
    public int CustodyId { get; set; }

    public DateOnly CountedOn { get; set; }
    public int CountedBy { get; set; }
    public DateTime CountedAt { get; set; }

    /// <summary>The recorded (ledger) balance when the count was saved, computed by the server.</summary>
    public decimal RecordedBalance { get; set; }
    public decimal CountedAmount { get; set; }

    /// <summary>Counted − recorded: positive = more cash than recorded.</summary>
    public decimal Difference { get; set; }

    public string? Explanation { get; set; }
    public Guid IdempotencyKey { get; set; }

    private OfficeCashCountStatus _status = OfficeCashCountStatus.Submitted;

    public OfficeCashCountStatus Status { get => _status; init => _status = value; }

    public int? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public long? AdjustmentEntryId { get; set; }

    public byte[] RowVersion { get; set; } = [];

    private static readonly StatusFlow<OfficeCashCountStatus> Flow = new(
        (OfficeCashCountStatus.Submitted, OfficeCashCountStatus.Accepted),
        (OfficeCashCountStatus.Submitted, OfficeCashCountStatus.Adjusted));

    public void MoveTo(OfficeCashCountStatus target) => _status = Flow.Move(_status, target);
}

internal enum OfficeCashCountStatus
{
    /// <summary>The cash matched the recorded balance: nothing to review.</summary>
    Matched,

    /// <summary>A difference waits for a reviewer.</summary>
    Submitted,

    /// <summary>The reviewer accepted the explanation; the balance is unchanged.</summary>
    Accepted,

    /// <summary>The reviewer posted the difference to the ledger as an adjustment.</summary>
    Adjusted,
}

/// <summary>
/// One alert to one person about an office record (a new expense to review, funds paid out, a low balance). An audited
/// row: the Platform outbox turns its insert into a stored notification (and push) after the commit, so nobody hears of
/// something that rolled back. The subclasses give each kind its own notification text.
/// </summary>
internal abstract class OfficeAlert
{
    public int Id { get; set; }
    public int CustodyId { get; set; }

    /// <summary>The record the alert is about (one of <see cref="OfficeAttachmentOwners"/>, or the custody itself).</summary>
    public string SourceType { get; set; } = "";
    public int SourceId { get; set; }

    public int RecipientUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

internal sealed class OfficeExpenseSubmittedAlert : OfficeAlert;

internal sealed class OfficeExpensePostedAlert : OfficeAlert;

internal sealed class OfficeFundingRequestedAlert : OfficeAlert;

internal sealed class OfficeFundsDisbursedAlert : OfficeAlert;

internal sealed class OfficeLowBalanceAlert : OfficeAlert;

internal sealed class OfficeDiscrepancyAlert : OfficeAlert;

internal sealed class OfficeSpendRequestedAlert : OfficeAlert;

/// <summary>The custody itself as an alert source (a low balance).</summary>
internal static class OfficeAlertSources
{
    public const string Custody = "OfficeCustody";
}
