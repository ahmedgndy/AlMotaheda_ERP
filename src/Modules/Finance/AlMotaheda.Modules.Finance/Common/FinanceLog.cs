using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Finance.Common;

/// <summary>Structured events of the Finance module (event ids 8001 to 8999). Ids only: never names or amounts.</summary>
internal sealed partial class FinanceLog(ILogger<FinanceLog> logger)
{
    private readonly ILogger<FinanceLog> _logger = logger;

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "User {ActorId} submitted payment request {RequestId} ({SourceType})")]
    public partial void Submitted(int actorId, int requestId, string sourceType);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Information, Message = "User {ActorId} moved payment request {RequestId}: {Action}, {OldStatus} -> {NewStatus}")]
    public partial void Moved(int actorId, int requestId, string action, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action} on payment request {RequestId}: {Reason}")]
    public partial void Refused(int actorId, int requestId, string action, string reason);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Information, Message = "Payment request {RequestId} reversed by user {ActorId} ({SourceType} payment {SourcePaymentId})")]
    public partial void Reversed(int actorId, int requestId, string sourceType, int sourcePaymentId);

    [LoggerMessage(EventId = 8011, Level = LogLevel.Information, Message = "User {ActorId} set the opening balance of treasury account {Account}")]
    public partial void OpeningBalanceSet(int actorId, string account);

    [LoggerMessage(EventId = 8012, Level = LogLevel.Information, Message = "User {ActorId} recorded treasury deposit {DepositId} into {Account}")]
    public partial void DepositRecorded(int actorId, int depositId, string account);

    [LoggerMessage(EventId = 8021, Level = LogLevel.Information, Message = "User {ActorId} opened custody account {AccountId} ({Kind}) for holder {HolderUserId}")]
    public partial void CustodyAccountOpened(int actorId, int accountId, string kind, int? holderUserId);

    [LoggerMessage(EventId = 8022, Level = LogLevel.Information, Message = "User {ActorId} closed custody account {AccountId}")]
    public partial void CustodyAccountClosed(int actorId, int accountId);

    [LoggerMessage(EventId = 8031, Level = LogLevel.Information, Message = "User {ActorId} requested custody transfer {TransferId} ({Purpose})")]
    public partial void TransferRequested(int actorId, int transferId, string purpose);

    [LoggerMessage(EventId = 8032, Level = LogLevel.Information, Message = "User {ActorId} moved custody transfer {TransferId}: {Action}, {OldStatus} -> {NewStatus}")]
    public partial void TransferMoved(int actorId, int transferId, string action, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 8033, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action} on custody transfer {TransferId}: {Reason}")]
    public partial void TransferRefused(int actorId, int transferId, string action, string reason);

    [LoggerMessage(EventId = 8041, Level = LogLevel.Information, Message = "User {ActorId} recorded cash count {CountId} on custody account {AccountId}")]
    public partial void CashCounted(int actorId, int accountId, int countId);

    [LoggerMessage(EventId = 8042, Level = LogLevel.Information, Message = "User {ActorId} adjusted custody account {AccountId} (adjustment {AdjustmentId})")]
    public partial void CustodyAdjusted(int actorId, int accountId, int adjustmentId);

    [LoggerMessage(EventId = 8062, Level = LogLevel.Information, Message = "User {ActorId} disputed adjustment {AdjustmentId} of custody account {AccountId}")]
    public partial void CustodyAdjustmentDisputed(int actorId, int accountId, int adjustmentId);

    [LoggerMessage(EventId = 8063, Level = LogLevel.Information, Message = "User {ActorId} resolved adjustment {AdjustmentId} of custody account {AccountId}: {Status}")]
    public partial void CustodyAdjustmentResolved(int actorId, int accountId, int adjustmentId, string status);

    [LoggerMessage(EventId = 8043, Level = LogLevel.Information, Message = "User {ActorId} closed the period of custody account {AccountId} up to {PeriodEnd}")]
    public partial void PeriodClosed(int actorId, int accountId, DateOnly periodEnd);

    [LoggerMessage(EventId = 8051, Level = LogLevel.Information, Message = "User {ActorId} created payroll batch {BatchId} ({Kind})")]
    public partial void BatchCreated(int actorId, int batchId, string kind);

    [LoggerMessage(EventId = 8052, Level = LogLevel.Information, Message = "User {ActorId} closed payroll batch {BatchId}")]
    public partial void BatchClosed(int actorId, int batchId);

    [LoggerMessage(EventId = 8061, Level = LogLevel.Information, Message = "Payment request {RequestId} paid out of custody account {AccountId} by user {ActorId}")]
    public partial void PaidFromCustody(int actorId, int requestId, int accountId);
}
