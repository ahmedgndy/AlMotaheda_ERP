using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.HR.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Features.Gps;

/// <summary>
/// One position from the phone's queue: the id the phone gave it, the time it took it, what it measured, and the
/// assignment the phone believed active when it took it (optional: the server resolves it otherwise).
/// </summary>
internal sealed record VehiclePointRequest(
    string? Id,
    DateTime? RecordedAt,
    decimal? Latitude,
    decimal? Longitude,
    int? AccuracyMeters,
    decimal? SpeedMps,
    decimal? Heading,
    decimal? AltitudeMeters,
    long? Sequence,
    int? AssignmentId);

/// <summary>One heartbeat from the phone: only what it could measure when it took it.</summary>
internal sealed record VehicleHeartbeatRequest(
    DateTime? RecordedAt, int? BatteryPercent, bool? Charging, string? TrackingState, string? Permission, bool? GpsEnabled, int? PendingPoints);

internal sealed record VehicleTrackingBatchRequest(
    string? DeviceId, string? AppVersion, IReadOnlyList<VehiclePointRequest?>? Points, IReadOnlyList<VehicleHeartbeatRequest?>? Heartbeats);

/// <summary>A position refused for good: the phone marks it with the reason and stops sending it.</summary>
internal sealed record RejectedVehiclePoint(string? Id, string Reason);

/// <param name="Accepted">Positions stored now.</param>
/// <param name="Duplicates">Positions an earlier upload already stored (the phone may forget them).</param>
/// <param name="Rejected">Positions refused for good, with the reason.</param>
internal sealed record VehicleTrackingBatchResult(int Accepted, int Duplicates, IReadOnlyList<RejectedVehiclePoint> Rejected, int NextInSeconds, DateTime ServerTime);

/// <summary>The vehicle a driver is tracked for right now.</summary>
internal sealed record GpsTrackedVehicle(int VehicleId, int AssignmentId, string Plate, string NameAr, string NameEn, DateOnly StartDate);

/// <summary>
/// Vehicle tracking from the driver's phone. After the driver accepts the tracking notice once on the phone, the
/// phone records the assigned vehicle's position about once a minute and uploads its queue when the server can be
/// reached. Every position is stored once, with the time the phone took it, against the vehicle, assignment and
/// driver that covered that time; the client never chooses a vehicle the caller wasn't assigned to.
/// </summary>
internal sealed class DriverTracking(
    FleetDbContext db,
    IEmployeeDirectory employees,
    VehicleTrackingAlerts alerts,
    CompanyClock clock,
    IOptions<FleetOptions> options,
    FleetLog log)
{
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);
    private const int MaxAccuracyMeters = 100_000;
    private const int MaxHeartbeats = 200;

    public static readonly string[] Platforms = ["Android", "iOS", "macOS", "Windows", "Linux"];
    public static readonly string[] TrackingStates = ["Running", "PermissionDenied", "BackgroundDenied", "GpsOff", "Stopped"];
    public static readonly string[] Permissions = ["Always", "WhileInUse", "Denied"];

    private VehicleGpsOptions Settings => options.Value.Gps;

    /// <summary>
    /// The vehicle the user is tracked for now: they are an active employee with an open assignment that has started,
    /// on a vehicle with tracking on that is in service or under maintenance. Otherwise <c>null</c>.
    /// </summary>
    public async Task<GpsTrackedVehicle?> CurrentAsync(int userId, CancellationToken cancellationToken)
    {
        var employee = await employees.FindByUserAsync(userId, cancellationToken);
        if (employee is not { IsActive: true })
        {
            return null;
        }

        var today = clock.Today;
        return await db.VehicleDriverAssignments.AsNoTracking()
            .Where(assignment => assignment.DriverEmployeeId == employee.Id && assignment.EndDate == null && assignment.StartDate <= today
                                 && assignment.Vehicle.TrackingEnabled
                                 && (assignment.Vehicle.Equipment.Status == EquipmentStatus.InService || assignment.Vehicle.Equipment.Status == EquipmentStatus.UnderMaintenance))
            .OrderByDescending(assignment => assignment.StartDate)
            .Select(assignment => new GpsTrackedVehicle(
                assignment.VehicleId, assignment.Id, assignment.Vehicle.Plate, assignment.Vehicle.Equipment.NameAr, assignment.Vehicle.Equipment.NameEn, assignment.StartDate))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The phone's row when it accepted the current notice version; otherwise <c>null</c>.</summary>
    public Task<VehicleTrackingDevice?> AcceptedDeviceAsync(int userId, string deviceId, CancellationToken cancellationToken)
    {
        var policy = Settings.PolicyVersion;
        return db.VehicleTrackingDevices.FirstOrDefaultAsync(device => device.UserId == userId && device.DeviceId == deviceId && device.PolicyVersion >= policy, cancellationToken);
    }

    public Task<VehicleTrackingDevice?> DeviceAsync(int userId, string deviceId, CancellationToken cancellationToken) =>
        db.VehicleTrackingDevices.AsNoTracking().FirstOrDefaultAsync(device => device.UserId == userId && device.DeviceId == deviceId, cancellationToken);

    /// <summary>Records that the driver accepted the notice on this phone. Saved here.</summary>
    public async Task<VehicleTrackingDevice> AcceptPolicyAsync(int userId, string deviceId, string platform, string? appVersion, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var device = await db.VehicleTrackingDevices.FirstOrDefaultAsync(row => row.UserId == userId && row.DeviceId == deviceId, cancellationToken);
            if (device is null)
            {
                device = new VehicleTrackingDevice { UserId = userId, DeviceId = deviceId };
                db.VehicleTrackingDevices.Add(device);
            }

            device.Platform = platform;
            device.AppVersion = Truncate(appVersion);
            if (device.PolicyVersion < Settings.PolicyVersion)
            {
                device.PolicyVersion = Settings.PolicyVersion;
                device.PolicyAcceptedAt = clock.UtcNow;
            }

            if (await TrySaveAsync(cancellationToken))
            {
                log.TrackingPolicyAccepted(userId, device.PolicyVersion, device.Id);
                return device;
            }

            if (attempt > 0)
            {
                throw new DbUpdateConcurrencyException("The tracking device row kept changing.");
            }
        }
    }

    /// <summary>Validates and stores one queue upload. Returns <c>null</c> when the request itself is invalid (see <paramref name="errors"/>).</summary>
    public async Task<VehicleTrackingBatchResult?> RecordAsync(int userId, VehicleTrackingDevice device, VehicleTrackingBatchRequest request, ValidationErrors errors, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var points = request.Points ?? [];
        var heartbeats = request.Heartbeats ?? [];
        if (points.Count > settings.MaxBatchPoints)
        {
            errors.Add("points", $"Send at most {settings.MaxBatchPoints} positions at a time.");
        }

        if (heartbeats.Count > MaxHeartbeats)
        {
            errors.Add("heartbeats", $"Send at most {MaxHeartbeats} heartbeats at a time.");
        }

        if (errors.HasErrors)
        {
            return null;
        }

        var now = clock.UtcNow;
        var oldest = now.AddDays(-settings.MaxLateDays);
        var employee = await employees.FindByUserAsync(userId, cancellationToken);
        var assignments = employee is null
            ? []
            : await db.VehicleDriverAssignments.AsNoTracking()
                .Where(assignment => assignment.DriverEmployeeId == employee.Id)
                .Select(assignment => new AssignmentWindow(assignment.Id, assignment.VehicleId, assignment.StartDate, assignment.EndDate, assignment.AssignedAt, assignment.EndedAt, assignment.Vehicle.TrackingEnabled))
                .ToListAsync(cancellationToken);

        var rejected = new List<RejectedVehiclePoint>();
        var valid = new List<VehicleGpsPoint>();
        var seen = new HashSet<Guid>();
        foreach (var point in points)
        {
            if (Reject(point, now, oldest) is { } reason)
            {
                rejected.Add(new RejectedVehiclePoint(point?.Id, reason));
                continue;
            }

            var recordedAt = point!.RecordedAt!.Value.ToUniversalTime();
            var (assignment, refusal) = employee is null ? (null, "notADriver") : Resolve(assignments, point.AssignmentId, recordedAt);
            if (assignment is null)
            {
                rejected.Add(new RejectedVehiclePoint(point.Id, refusal!));
                continue;
            }

            var id = Guid.Parse(point.Id!.Trim());
            if (!seen.Add(id))
            {
                continue;
            }

            valid.Add(new VehicleGpsPoint
            {
                VehicleId = assignment.VehicleId,
                AssignmentId = assignment.Id,
                DriverEmployeeId = employee!.Id,
                UserId = userId,
                DeviceId = device.Id,
                ClientPointId = id,
                Sequence = point.Sequence is >= 0 ? point.Sequence : null,
                RecordedAt = recordedAt,
                ReceivedAt = now,
                Latitude = Math.Round(point.Latitude!.Value, 6),
                Longitude = Math.Round(point.Longitude!.Value, 6),
                AccuracyMeters = point.AccuracyMeters,
                SpeedKmh = point.SpeedMps is >= 0 and <= 120 ? Math.Round(point.SpeedMps.Value * 3.6m, 1) : null,
                HeadingDegrees = point.Heading is >= 0 and <= 360 ? (short)Math.Round(point.Heading.Value) : null,
                AltitudeMeters = point.AltitudeMeters is >= -1000 and <= 10_000 ? Math.Round(point.AltitudeMeters.Value, 1) : null,
            });
        }

        var beats = heartbeats
            .Where(beat => beat?.RecordedAt is { } at && at.ToUniversalTime() <= now + MaxClockSkew && at.ToUniversalTime() >= oldest)
            .Select(beat => beat!)
            .OrderBy(beat => beat.RecordedAt!.Value.ToUniversalTime())
            .ToList();
        var ids = valid.Select(point => point.ClientPointId).ToList();

        for (var attempt = 0; ; attempt++)
        {
            var known = ids.Count == 0
                ? []
                : await db.VehicleGpsPoints.AsNoTracking()
                    .Where(point => point.UserId == userId && ids.Contains(point.ClientPointId))
                    .Select(point => point.ClientPointId)
                    .ToListAsync(cancellationToken);
            var fresh = valid.Where(point => !known.Contains(point.ClientPointId)).OrderBy(point => point.RecordedAt).ToList();
            var duplicates = valid.Count - fresh.Count;

            db.VehicleGpsPoints.AddRange(fresh);
            await MoveStatesAsync(fresh, now, cancellationToken);
            var tracked = await db.VehicleTrackingDevices.FirstAsync(row => row.Id == device.Id, cancellationToken);
            tracked.LastUploadAt = now;
            if (Truncate(request.AppVersion) is { } version)
            {
                tracked.AppVersion = version;
            }

            ApplyHeartbeat(tracked, beats.LastOrDefault(), now);

            if (await TrySaveAsync(cancellationToken))
            {
                log.TrackingBatch(userId, fresh.Count, duplicates, rejected.Count, beats.Count);
                return new VehicleTrackingBatchResult(fresh.Count, duplicates, rejected, settings.HeartbeatMinutes * 60, now);
            }

            if (attempt > 1)
            {
                throw new DbUpdateConcurrencyException("The vehicle tracking rows kept changing.");
            }

            // Lost a race (the same queue sent twice at once, or the monitor): the tracker was cleared; try again.
            foreach (var point in valid)
            {
                point.Id = 0;
            }
        }
    }

    private sealed record AssignmentWindow(int Id, int VehicleId, DateOnly StartDate, DateOnly? EndDate, DateTime AssignedAt, DateTime? EndedAt, bool TrackingEnabled)
    {
        /// <summary>
        /// The stay covers a position of [day] taken at [recordedAt]. On its last day a stay ended at a known moment
        /// covers only what was taken before that moment: after a hand-over the former driver's phone no longer moves
        /// the vehicle, while what it recorded (and queued offline) before the hand-over still belongs to the stay.
        /// </summary>
        public bool Covers(DateOnly day, DateTime recordedAt) =>
            StartDate <= day && (EndDate is null || EndDate > day || (EndDate == day && (EndedAt is null || recordedAt < EndedAt)));
    }

    /// <summary>
    /// The caller's assignment that covered the capture time (company day). The phone's own tag wins when it names
    /// one of the caller's assignments covering that day (a driver who changed vehicles that day). A tag naming
    /// someone else's assignment is refused; a tag of the caller's that doesn't cover the day (the phone kept an old
    /// assignment while offline) is ignored and the day decides.
    /// </summary>
    private (AssignmentWindow? Assignment, string? Refusal) Resolve(List<AssignmentWindow> assignments, int? taggedId, DateTime recordedAt)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(recordedAt, clock.Zone));
        var covering = assignments.Where(assignment => assignment.Covers(day, recordedAt)).ToList();
        if (taggedId is { } tag && assignments.All(assignment => assignment.Id != tag))
        {
            return (null, "assignment");
        }

        var chosen = covering.FirstOrDefault(assignment => assignment.Id == taggedId);
        if (chosen is null)
        {
            chosen = covering
                .OrderByDescending(assignment => assignment.AssignedAt <= recordedAt)
                .ThenByDescending(assignment => assignment.StartDate)
                .ThenByDescending(assignment => assignment.Id)
                .FirstOrDefault();
            if (chosen is null)
            {
                return (null, "noAssignment");
            }
        }

        return chosen.TrackingEnabled ? (chosen, null) : (null, "trackingOff");
    }

    /// <summary>Only a newer position moves a vehicle's latest state; a live one ends its silence (and its alerts).</summary>
    private async Task MoveStatesAsync(List<VehicleGpsPoint> fresh, DateTime now, CancellationToken cancellationToken)
    {
        if (fresh.Count == 0)
        {
            return;
        }

        var vehicleIds = fresh.Select(point => point.VehicleId).Distinct().ToList();
        var states = await db.VehicleTrackingStates.Where(state => vehicleIds.Contains(state.VehicleId)).ToListAsync(cancellationToken);
        foreach (var vehicleId in vehicleIds)
        {
            var state = states.FirstOrDefault(row => row.VehicleId == vehicleId);
            if (state is null)
            {
                state = new VehicleTrackingState { VehicleId = vehicleId };
                db.VehicleTrackingStates.Add(state);
            }

            var latest = fresh.Where(point => point.VehicleId == vehicleId).MaxBy(point => point.RecordedAt)!;
            if (state.LastPointAt is null || latest.RecordedAt >= state.LastPointAt)
            {
                state.LastPointAt = latest.RecordedAt;
                state.LastReceivedAt = now;
                state.LastLatitude = latest.Latitude;
                state.LastLongitude = latest.Longitude;
                state.LastAccuracyMeters = latest.AccuracyMeters;
                state.LastSpeedKmh = latest.SpeedKmh;
                state.LastDriverEmployeeId = latest.DriverEmployeeId;
            }

            // A silence ends when the server hears from the vehicle again with a position taken recently.
            if (state.SilentSince is not null && now - latest.RecordedAt <= TimeSpan.FromMinutes(Settings.StaleMinutes))
            {
                state.SilentSince = null;
                await alerts.CloseAsync(vehicleId, VehicleTrackingAlertStatus.Recovered, now, cancellationToken);
                log.VehicleReporting(vehicleId);
            }
        }
    }

    private static void ApplyHeartbeat(VehicleTrackingDevice device, VehicleHeartbeatRequest? latest, DateTime now)
    {
        if (latest is null)
        {
            return;
        }

        var recordedAt = latest.RecordedAt!.Value.ToUniversalTime();
        if (device.LastHeartbeatAt is { } last && recordedAt < last)
        {
            return;
        }

        device.LastHeartbeatAt = recordedAt;
        device.LastHeartbeatReceivedAt = now;
        device.BatteryPercent = latest.BatteryPercent is >= 0 and <= 100 ? latest.BatteryPercent : null;
        device.BatteryCharging = latest.Charging;
        device.TrackingState = TrackingStates.Contains(latest.TrackingState) ? latest.TrackingState : null;
        device.Permission = Permissions.Contains(latest.Permission) ? latest.Permission : null;
        device.GpsEnabled = latest.GpsEnabled;
        device.PendingPoints = latest.PendingPoints is >= 0 ? latest.PendingPoints : null;
    }

    /// <summary>Why one queued position is refused for good, or <c>null</c> when it is fine.</summary>
    internal static string? Reject(VehiclePointRequest? point, DateTime now, DateTime oldest)
    {
        if (point is null || !Guid.TryParse(point.Id?.Trim(), out var id) || id == Guid.Empty)
        {
            return "id";
        }

        if (point.Latitude is not { } latitude || latitude is < -90 or > 90 || point.Longitude is not { } longitude || longitude is < -180 or > 180
            || (latitude == 0 && longitude == 0))
        {
            return "position";
        }

        if (point.AccuracyMeters is < 0 or > MaxAccuracyMeters)
        {
            return "accuracy";
        }

        if (point.RecordedAt is not { } recorded)
        {
            return "recordedAt";
        }

        var utc = recorded.ToUniversalTime();
        return utc > now + MaxClockSkew ? "future" : utc < oldest ? "tooOld" : null;
    }

    private static string? Truncate(string? appVersion) =>
        Input.Clean(appVersion) is { } version
            ? version.Length > VehicleTrackingDeviceConfiguration.AppVersionMaxLength ? version[..VehicleTrackingDeviceConfiguration.AppVersionMaxLength] : version
            : null;

    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
                                                   || SqlViolations.IsUnique(exception, VehicleGpsPointConfiguration.ClientPointIndex)
                                                   || SqlViolations.IsUnique(exception, VehicleTrackingDeviceConfiguration.UserDeviceIndex)
                                                   || SqlViolations.IsUnique(exception, VehicleTrackingStateConfiguration.PrimaryKey)
                                                   || SqlViolations.IsUnique(exception, VehicleTrackingAlertConfiguration.OpenIndex))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
