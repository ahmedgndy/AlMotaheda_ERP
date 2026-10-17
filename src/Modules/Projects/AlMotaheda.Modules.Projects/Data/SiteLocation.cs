
namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// One site engineer's location sharing on one project: whether they turned it on, and the last position their
/// phone sent. One row per project and engineer, created the first time they turn sharing on.
/// </summary>
/// <remarks>
/// <para>Owner's rules (2026-10-17): only site engineers on their assigned project, only during working hours, only
/// after they turn it on in the app; visible only to the project manager and Executives.</para>
/// <para>Not audited: positions are personal data and change every few minutes. Turning sharing on and off is in the
/// application log, and the alerts it causes (<see cref="LocationAlert"/>) are audited.</para>
/// </remarks>
[NotAudited]
internal sealed class LocationSharing
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The engineer (Access user).</summary>
    public int UserId { get; set; }

    public bool IsOn { get; set; }
    public DateTime? TurnedOnAt { get; set; }
    public DateTime? TurnedOffAt { get; set; }

    /// <summary>When the phone took the last position it sent (UTC).</summary>
    public DateTime? LastPingAt { get; set; }
    public decimal? LastLatitude { get; set; }
    public decimal? LastLongitude { get; set; }
    public int? LastAccuracyMeters { get; set; }

    /// <summary>Distance from the site centre; empty when the project has no site position.</summary>
    public int? LastDistanceMeters { get; set; }

    /// <summary>Set when no position arrived for too long during working hours; cleared by the next position.</summary>
    public DateTime? UnavailableSince { get; set; }

    /// <summary>Set when the positions say the engineer is clearly outside the site area.</summary>
    public DateTime? OutsideSince { get; set; }

    /// <summary>Positions in a row that were clearly outside the area. One stray position doesn't raise an alert.</summary>
    public int OutsidePings { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One position a phone sent. Kept for a set number of days (default 90), then deleted.</summary>
[NotAudited]
internal sealed class LocationPing
{
    public long Id { get; set; }

    public int ProjectId { get; set; }
    public int UserId { get; set; }

    /// <summary>When the phone took the position (UTC).</summary>
    public DateTime RecordedAt { get; set; }

    /// <summary>When the server received it (UTC).</summary>
    public DateTime ReceivedAt { get; set; }

    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int? AccuracyMeters { get; set; }
    public int? DistanceMeters { get; set; }

    /// <summary>Empty when the project has no site position or the position was too rough to tell.</summary>
    public bool? InsideSite { get; set; }
}

/// <summary>
/// One alert to one person (the project manager or an Executive) about one engineer. Opened once per outage and
/// closed when it is over, so each outage notifies once and its end notifies once. Holds no position.
/// </summary>
/// <remarks>
/// One table; the kind is the class (column <c>Kind</c>), so the audit trail and the notification rules see
/// <see cref="LocationUnavailableAlert"/> and <see cref="OutsideSiteAlert"/> as different records.
/// </remarks>
internal abstract class LocationAlert
{
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>The engineer the alert is about.</summary>
    public int EngineerUserId { get; set; }

    /// <summary>Who is told: the project's manager or an Executive.</summary>
    public int RecipientUserId { get; set; }

    public LocationAlertStatus Status { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>No position for too long during working hours. Only that: the app never guesses why.</summary>
internal sealed class LocationUnavailableAlert : LocationAlert;

/// <summary>The positions say the engineer is clearly outside the site area.</summary>
internal sealed class OutsideSiteAlert : LocationAlert;

/// <summary>Stored by name; the notification rules read <c>Status</c>.</summary>
internal enum LocationAlertStatus
{
    /// <summary>The outage is on.</summary>
    Open,

    /// <summary>A position arrived again, or the engineer is back in the area. Notifies.</summary>
    Recovered,

    /// <summary>The engineer turned sharing off or left the project. Closes without a notification.</summary>
    Ended,
}
