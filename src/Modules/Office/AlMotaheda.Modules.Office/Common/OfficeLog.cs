using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Office.Common;

/// <summary>Structured events of the Office module (event ids 12001 to 12999). Ids only: never names, vendors or amounts.</summary>
internal sealed partial class OfficeLog(ILogger<OfficeLog> logger)
{
    private readonly ILogger<OfficeLog> _logger = logger;

    [LoggerMessage(EventId = 12001, Level = LogLevel.Information, Message = "User {ActorId} created custody {CustodyId}")]
    public partial void CustodyCreated(int actorId, int custodyId);

    [LoggerMessage(EventId = 12002, Level = LogLevel.Information, Message = "User {ActorId} updated custody {CustodyId}")]
    public partial void CustodyUpdated(int actorId, int custodyId);

    [LoggerMessage(EventId = 12003, Level = LogLevel.Information, Message = "User {ActorId} moved custody {CustodyId} to {Status}")]
    public partial void CustodyMoved(int actorId, int custodyId, string status);

    [LoggerMessage(EventId = 12011, Level = LogLevel.Information, Message = "User {ActorId} posted ledger entry {EntryId} ({Kind}) on custody {CustodyId}")]
    public partial void EntryPosted(int actorId, int custodyId, long entryId, string kind);

    [LoggerMessage(EventId = 12012, Level = LogLevel.Information, Message = "Custody {CustodyId} fell below its low-balance threshold; {Recipients} people told")]
    public partial void LowBalance(int custodyId, int recipients);

    [LoggerMessage(EventId = 12021, Level = LogLevel.Information, Message = "User {ActorId} uploaded office attachment {AttachmentId} ({Purpose})")]
    public partial void AttachmentUploaded(int actorId, int attachmentId, string purpose);

    [LoggerMessage(EventId = 12022, Level = LogLevel.Warning, Message = "User {ActorId} upload refused: {Code}")]
    public partial void UploadRefused(int actorId, string code);

    [LoggerMessage(EventId = 12023, Level = LogLevel.Error, Message = "The office file store failed")]
    public partial void StorageFailed(Exception exception);

    [LoggerMessage(EventId = 12024, Level = LogLevel.Warning, Message = "Stored office file {StorageKey} could not be removed after a failed save")]
    public partial void StoredFileNotRemoved(Exception exception, string storageKey);

    [LoggerMessage(EventId = 12031, Level = LogLevel.Information, Message = "User {ActorId} submitted office expense {ExpenseId} (round {Round}, approvals needed {Required})")]
    public partial void ExpenseSubmitted(int actorId, int expenseId, int round, int required);

    [LoggerMessage(EventId = 12032, Level = LogLevel.Information, Message = "User {ActorId} moved office expense {ExpenseId} to {Status}")]
    public partial void ExpenseMoved(int actorId, int expenseId, string status);

    [LoggerMessage(EventId = 12033, Level = LogLevel.Information, Message = "User {ActorId} reversed office expense {ExpenseId}")]
    public partial void ExpenseReversed(int actorId, int expenseId);

    [LoggerMessage(EventId = 12034, Level = LogLevel.Information, Message = "Office expense {ExpenseId} opened price case {ExceptionId}")]
    public partial void PriceCaseOpened(int expenseId, int exceptionId);

    [LoggerMessage(EventId = 12035, Level = LogLevel.Information, Message = "Price case {ExceptionId} of office expense {ExpenseId} decided: {Status}")]
    public partial void PriceCaseDecided(int expenseId, int exceptionId, string status);

    [LoggerMessage(EventId = 12041, Level = LogLevel.Information, Message = "User {ActorId} moved funding request {FundingId} to {Status}")]
    public partial void FundingMoved(int actorId, int fundingId, string status);

    [LoggerMessage(EventId = 12042, Level = LogLevel.Information, Message = "User {ActorId} asked Finance to pay funding request {FundingId} (payment request {RequestId})")]
    public partial void FundingPaymentRequested(int actorId, int fundingId, int requestId);

    [LoggerMessage(EventId = 12051, Level = LogLevel.Information, Message = "User {ActorId} moved spend request {SpendId} to {Status}")]
    public partial void SpendMoved(int actorId, int spendId, string status);

    [LoggerMessage(EventId = 12061, Level = LogLevel.Information, Message = "User {ActorId} recorded cash count {CountId} on custody {CustodyId} ({Status})")]
    public partial void CashCounted(int actorId, int custodyId, int countId, string status);

    [LoggerMessage(EventId = 12062, Level = LogLevel.Information, Message = "User {ActorId} moved cash count {CountId} to {Status}")]
    public partial void CashCountMoved(int actorId, int countId, string status);

    [LoggerMessage(EventId = 12071, Level = LogLevel.Information, Message = "User {ActorId} recorded cash return {ReturnId} on custody {CustodyId}")]
    public partial void CashReturned(int actorId, int custodyId, int returnId);

    [LoggerMessage(EventId = 12091, Level = LogLevel.Information, Message = "Custody {CustodyId} opened month {Year}-{Month} (period {PeriodId})")]
    public partial void PeriodOpened(int custodyId, int periodId, int year, int month);

    [LoggerMessage(EventId = 12092, Level = LogLevel.Information, Message = "Period {PeriodId} of custody {CustodyId} moved to {Status}")]
    public partial void PeriodMoved(int custodyId, int periodId, string status);

    [LoggerMessage(EventId = 12093, Level = LogLevel.Information, Message = "User {ActorId} settled period {PeriodId} ({Settlement})")]
    public partial void PeriodSettled(int actorId, int periodId, string settlement);

    [LoggerMessage(EventId = 12094, Level = LogLevel.Information, Message = "User {ActorId} authorized the allocation of period {PeriodId} (funding request {FundingId})")]
    public partial void AllocationAuthorized(int actorId, int periodId, int fundingId);

    [LoggerMessage(EventId = 12095, Level = LogLevel.Warning, Message = "Period {PeriodId}: the monthly allocation was not requested automatically: {Reason}")]
    public partial void AllocationNotRequested(int periodId, string reason);

    [LoggerMessage(EventId = 12096, Level = LogLevel.Error, Message = "The office month check failed")]
    public partial void PeriodCheckFailed(Exception exception);

    [LoggerMessage(EventId = 12081, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action}: {Reason}")]
    public partial void ActionRefused(int actorId, string action, string reason);

    [LoggerMessage(EventId = 12082, Level = LogLevel.Information, Message = "User {ActorId} wrote Finance custody entry {EntryId} on office custody {CustodyId} ({SourceType} {SourceId})")]
    public partial void FinanceCustodyWritten(int actorId, int custodyId, long entryId, string sourceType, int sourceId);
}
