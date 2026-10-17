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

    [LoggerMessage(EventId = 7021, Level = LogLevel.Warning, Message = "Live updates could not be sent for {Count} messages; clients catch up when they refetch")]
    public partial void LiveUpdatesFailed(Exception exception, int count);

    [LoggerMessage(EventId = 7031, Level = LogLevel.Information, Message = "Device {DeviceId} registered for push by user {UserId}")]
    public partial void DeviceRegistered(long deviceId, int userId);

    [LoggerMessage(EventId = 7032, Level = LogLevel.Information, Message = "Device {DeviceId} moved from user {PreviousUserId} to user {UserId}")]
    public partial void DeviceMoved(long deviceId, int previousUserId, int userId);

    [LoggerMessage(EventId = 7033, Level = LogLevel.Information, Message = "User {UserId} unregistered a device from push")]
    public partial void DeviceUnregistered(int userId);

    [LoggerMessage(EventId = 7041, Level = LogLevel.Warning, Message = "Push notifications are off: {Reason}")]
    public partial void PushOff(string reason);

    [LoggerMessage(EventId = 7042, Level = LogLevel.Information, Message = "Push sent for notification {NotificationId} to {DeviceCount} device(s)")]
    public partial void PushSent(long notificationId, int deviceCount);

    [LoggerMessage(EventId = 7043, Level = LogLevel.Warning, Message = "Push for notification {NotificationId} failed on device {DeviceId} ({Status})")]
    public partial void PushFailed(long notificationId, long deviceId, string status);

    [LoggerMessage(EventId = 7044, Level = LogLevel.Information, Message = "Device {DeviceId} disabled: Firebase says its token is no longer valid")]
    public partial void DeviceDisabled(long deviceId);
}
