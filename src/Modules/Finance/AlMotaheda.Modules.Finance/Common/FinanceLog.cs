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
}
