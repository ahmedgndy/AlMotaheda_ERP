using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Platform.Common;

/// <summary>Structured events of the Platform module (event ids 7001 to 7999). Ids only: never titles or payloads.</summary>
internal sealed partial class PlatformLog(ILogger<PlatformLog> logger)
{
    private readonly ILogger<PlatformLog> _logger = logger;

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Outbox dispatch failed; retrying in {DelaySeconds} s")]
    public partial void DispatchFailed(Exception exception, double delaySeconds);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Information, Message = "Outbox dispatch works again after {FailureCount} failure(s)")]
    public partial void DispatchRecovered(int failureCount);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Error, Message = "Outbox message {MessageId} failed (attempt {Attempt})")]
    public partial void MessageFailed(Exception exception, long messageId, int attempt);

    [LoggerMessage(EventId = 7011, Level = LogLevel.Information, Message = "User {ActorId} marked {Count} notification(s) as read")]
    public partial void NotificationsRead(int actorId, int count);
}
