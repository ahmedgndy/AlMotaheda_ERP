using AlMotaheda.BuildingBlocks.Data;

namespace AlMotaheda.Modules.Fleet.Data;

/// <summary>
/// One position taken by the phone of a vehicle's driver while the vehicle was assigned to them. Raw and kept as
/// taken: the time the phone took it (<see cref="RecordedAt"/>), the time the server received it, and the quality
/// the phone reported. Which samples count for a route, a trip or a stop is decided when the history is read
/// (<c>TripSegmenter</c>), so late positions and changed thresholds never leave a stale summary behind.
/// </summary>
/// <remarks>
/// <para>The vehicle, the assignment and the driver are fixed at ingestion from the assignment that covered the
/// capture time, never from today's driver: a position taken offline before a hand-over keeps the old driver.</para>
/// <para>Not audited: positions are personal data and arrive every minute. They are deleted after
/// <c>Fleet:Gps:RetentionDays</c>.</para>
/// </remarks>
[NotAudited]
internal sealed class VehicleGpsPoint
{
    public long Id { get; set; }

    public int VehicleId { get; set; }

    /// <summary>The driver assignment that covered the capture time; the driver is that assignment's employee.</summary>
    public int AssignmentId { get; set; }
    public int DriverEmployeeId { get; set; }

    /// <summary>The Access user who uploaded it (the driver's account) and the phone it came from.</summary>
    public int UserId { get; set; }
    public int DeviceId { get; set; }

    /// <summary>The id the phone gave the position when it took it. A queue sent twice stores each position once.</summary>
    public Guid ClientPointId { get; set; }

    /// <summary>The phone's own counter for the position, when it sent one: a gap in it shows positions never received.</summary>
    public long? Sequence { get; set; }

    /// <summary>When the phone took the position (UTC). Never changed, however late it arrives.</summary>
    public DateTime RecordedAt { get; set; }

    /// <summary>When the server received it (UTC).</summary>
    public DateTime ReceivedAt { get; set; }

    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }

    /// <summary>The horizontal accuracy the phone reported (metres), when it reported one.</summary>
    public int? AccuracyMeters { get; set; }

    /// <summary>The speed the phone reported (km/h); never computed here.</summary>
    public decimal? SpeedKmh { get; set; }

    public short? HeadingDegrees { get; set; }
    public decimal? AltitudeMeters { get; set; }
}

/// <summary>
/// A driver's phone: when the driver accepted the vehicle tracking notice on it, and what its last heartbeat said
/// (battery, whether tracking runs, the location permission, GPS on or off, positions still waiting on the phone).
/// Only what the phone measured, with the time it measured it.
/// </summary>
[NotAudited]
internal sealed class VehicleTrackingDevice
{
    public int Id { get; set; }

    /// <summary>The driver (Access user).</summary>
    public int UserId { get; set; }

    /// <summary>The app's own id for this installation (not a hardware id).</summary>
    public string DeviceId { get; set; } = "";

    public string Platform { get; set; } = "";
    public string? AppVersion { get; set; }

    public int PolicyVersion { get; set; }
    public DateTime PolicyAcceptedAt { get; set; }

    /// <summary>When the phone took its last heartbeat (UTC), and when the server got it.</summary>
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? LastHeartbeatReceivedAt { get; set; }

    /// <summary>When the server last accepted an upload from this phone.</summary>
    public DateTime? LastUploadAt { get; set; }

    public int? BatteryPercent { get; set; }
    public bool? BatteryCharging { get; set; }

    /// <summary>What the phone said: <c>Running</c>, <c>PermissionDenied</c>, <c>BackgroundDenied</c>, <c>GpsOff</c>, <c>Stopped</c>.</summary>
    public string? TrackingState { get; set; }

    /// <summary><c>Always</c>, <c>WhileInUse</c> or <c>Denied</c>.</summary>
    public string? Permission { get; set; }

    public bool? GpsEnabled { get; set; }

    /// <summary>Positions still waiting on the phone at the last heartbeat.</summary>
    public int? PendingPoints { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// The latest position of one vehicle, kept apart from the vehicle row so a position every minute never touches the
/// vehicle's audit trail or row version. <see cref="SilentSince"/> is set by the monitor when an expected vehicle
/// stopped reporting, and cleared by the next live position.
/// </summary>
[NotAudited]
internal sealed class VehicleTrackingState
{
    /// <summary>The vehicle's id.</summary>
    public int VehicleId { get; set; }

    public DateTime? LastPointAt { get; set; }
    public DateTime? LastReceivedAt { get; set; }
    public decimal? LastLatitude { get; set; }
    public decimal? LastLongitude { get; set; }
    public int? LastAccuracyMeters { get; set; }
    public decimal? LastSpeedKmh { get; set; }
    public int? LastDriverEmployeeId { get; set; }

    /// <summary>Since when an expected vehicle has sent nothing (the last position, or when tracking was expected to start).</summary>
    public DateTime? SilentSince { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// One alert to one person (an Executive) that an expected vehicle stopped reporting positions. Opened once per
/// silence and closed when positions arrive again, so each outage notifies once and its end notifies once. It
/// never says why: the phone may be off, out of coverage, without a fix or without permission.
/// </summary>
internal sealed class VehicleTrackingAlert
{
    public int Id { get; set; }

    public int VehicleId { get; set; }

    /// <summary>The driver assigned when the silence started.</summary>
    public int? DriverEmployeeId { get; set; }

    public int RecipientUserId { get; set; }

    public VehicleTrackingAlertStatus Status { get; set; }

    /// <summary>The time of the last position before the silence (its capture time), when there was one.</summary>
    public DateTime? LastPointAt { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Stored by name; the notification rules read <c>Status</c>.</summary>
internal enum VehicleTrackingAlertStatus
{
    Open,

    /// <summary>Positions arrived again. Notifies.</summary>
    Recovered,

    /// <summary>Tracking is no longer expected (assignment ended, tracking turned off). Closes without a notification.</summary>
    Ended,
}
