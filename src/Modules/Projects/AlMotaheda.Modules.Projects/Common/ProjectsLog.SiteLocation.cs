using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Events of the site engineers' location sharing (event ids 2701 to 2799). Ids only: never positions.
/// </summary>
internal sealed partial class ProjectsLog
{
    [LoggerMessage(EventId = 2701, Level = LogLevel.Information, Message = "User {UserId} turned location sharing on for project {ProjectId}")]
    public partial void LocationSharingOn(int userId, int projectId);

    [LoggerMessage(EventId = 2702, Level = LogLevel.Information, Message = "User {UserId} turned location sharing off for project {ProjectId}")]
    public partial void LocationSharingOff(int userId, int projectId);

    [LoggerMessage(EventId = 2703, Level = LogLevel.Warning, Message = "Location of user {UserId} on project {ProjectId} unavailable since {SilentSince:o}; {RecipientCount} people alerted")]
    public partial void LocationUnavailable(int userId, int projectId, DateTime silentSince, int recipientCount);

    [LoggerMessage(EventId = 2704, Level = LogLevel.Information, Message = "Location of user {UserId} on project {ProjectId} is available again")]
    public partial void LocationBack(int userId, int projectId);

    [LoggerMessage(EventId = 2705, Level = LogLevel.Warning, Message = "User {UserId} is outside the site area of project {ProjectId}; {RecipientCount} people alerted")]
    public partial void OutsideSite(int userId, int projectId, int recipientCount);

    [LoggerMessage(EventId = 2706, Level = LogLevel.Information, Message = "User {UserId} is back in the site area of project {ProjectId}")]
    public partial void BackInsideSite(int userId, int projectId);

    [LoggerMessage(EventId = 2707, Level = LogLevel.Information, Message = "Deleted {Count} location positions older than {KeepDays} days")]
    public partial void LocationPingsDeleted(int count, int keepDays);

    [LoggerMessage(EventId = 2708, Level = LogLevel.Error, Message = "The location monitor failed; it tries again on its next round")]
    public partial void LocationMonitorFailed(Exception exception);

    [LoggerMessage(EventId = 2709, Level = LogLevel.Information, Message = "Location sharing of user {UserId} on project {ProjectId} was turned off: no longer a site engineer there")]
    public partial void LocationSharingEnded(int userId, int projectId);

    [LoggerMessage(EventId = 2710, Level = LogLevel.Information, Message = "User {UserId} accepted tracking policy version {PolicyVersion} on device row {DeviceRowId}")]
    public partial void TrackingPolicyAccepted(int userId, int policyVersion, int deviceRowId);

    [LoggerMessage(EventId = 2711, Level = LogLevel.Information, Message = "Tracking upload from user {UserId}: {Accepted} positions kept, {Duplicates} repeated, {Rejected} refused, {Heartbeats} heartbeats")]
    public partial void TrackingBatch(int userId, int accepted, int duplicates, int rejected, int heartbeats);

    [LoggerMessage(EventId = 2712, Level = LogLevel.Warning, Message = "Battery of user {UserId}'s phone is low; {RecipientCount} people alerted on project {ProjectId}")]
    public partial void LowBattery(int userId, int projectId, int recipientCount);

    [LoggerMessage(EventId = 2713, Level = LogLevel.Information, Message = "Battery of user {UserId}'s phone recovered")]
    public partial void BatteryRecovered(int userId);
}
