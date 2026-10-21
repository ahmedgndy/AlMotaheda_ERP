using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Features.SiteLocation;

/// <summary>One position from the phone's queue, with the id the phone gave it when it took it.</summary>
internal sealed record TrackedPointRequest(string? Id, DateTime? RecordedAt, decimal? Latitude, decimal? Longitude, int? AccuracyMeters);

/// <summary>One heartbeat from the phone: only what it could measure when it took it.</summary>
internal sealed record HeartbeatRequest(
    DateTime? RecordedAt,
    int? BatteryPercent,
    bool? Charging,
    string? TrackingState,
    string? Permission,
    bool? GpsEnabled,
    int? PendingPoints);

/// <summary>A queue upload: positions and heartbeats, possibly taken hours ago while the phone was offline.</summary>
internal sealed record TrackingBatchRequest(
    string? DeviceId,
    string? AppVersion,
    IReadOnlyList<TrackedPointRequest?>? Points,
    IReadOnlyList<HeartbeatRequest?>? Heartbeats);

/// <summary>A position the server refused for good: the phone deletes it from its queue and shows why.</summary>
internal sealed record RejectedPoint(string? Id, string Reason);

/// <param name="Accepted">Positions stored now.</param>
/// <param name="Duplicates">Positions already stored by an earlier upload (the phone may delete them).</param>
/// <param name="Rejected">Positions refused for good (bad values, too old).</param>
/// <param name="Projects">How many projects the positions were stored for.</param>
/// <param name="NextInSeconds">When the phone should send its next heartbeat.</param>
internal sealed record TrackingBatchResult(int Accepted, int Duplicates, IReadOnlyList<RejectedPoint> Rejected, int Projects, int NextInSeconds, DateTime ServerTime);

/// <summary>A project the engineer is tracked on: the site circle the app may show them.</summary>
internal sealed record TrackedProject(int ProjectId, string Code, string NameAr, string NameEn, decimal? SiteLatitude, decimal? SiteLongitude, int SiteRadiusMeters);

/// <summary>
/// Automatic tracking on the engineer's company phone (owner's brief of 2026-10-17, evening): after the engineer
/// accepts the tracking policy once on the phone, every project where they are an active site engineer is tracked,
/// and the phone uploads its queue of positions and heartbeats when it can reach the server.
/// </summary>
/// <remarks>
/// <para>Each position keeps the time the phone took it. A queue sent twice stores each position once (by the id
/// the phone gave it). Positions sent late go into the history; only live ones (see
/// <see cref="SiteLocationRules.IsLive"/>) open or close alerts. Leaving and coming back to the site carry the time the
/// phone took the position; an outage ("location unavailable") ends when the server receives positions again.</para>
/// <para>Battery: a live heartbeat at or below the low level, not charging, alerts once until it charges or
/// recovers. Nothing is ever said about the battery of a phone that stopped reporting.</para>
/// </remarks>
internal sealed class SiteLocationTracker(
    ProjectsDbContext db,
    SiteLocationAlerts alerts,
    CompanyClock clock,
    IOptions<SiteLocationOptions> options,
    ProjectsLog log)
{
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);
    private const int MaxAccuracyMeters = 100_000;
    private const int MaxHeartbeats = 200;

    private SiteLocationOptions Settings => options.Value;

    /// <summary>The non-completed projects where the user is an active site engineer, with their sites.</summary>
    public Task<List<TrackedProject>> TrackedProjectsAsync(int userId, CancellationToken cancellationToken) =>
        db.ProjectMembers.AsNoTracking()
            .Where(member => member.UserId == userId
                             && member.EndDate == null
                             && member.Responsibility == MemberResponsibility.SiteEngineer
                             && member.Project.Status != ProjectStatus.Completed)
            .Select(member => member.Project)
            .Distinct()
            .OrderBy(project => project.Code)
            .Select(project => new TrackedProject(
                project.Id, project.Code, project.NameAr, project.NameEn, project.SiteLatitude, project.SiteLongitude, project.SiteRadiusMeters))
            .ToListAsync(cancellationToken);

    /// <summary>The phone's row when it accepted the current policy version; otherwise <c>null</c>.</summary>
    public Task<LocationDevice?> AcceptedDeviceAsync(int userId, string deviceId, CancellationToken cancellationToken) =>
        db.LocationDevices.FirstOrDefaultAsync(
            device => device.UserId == userId && device.DeviceId == deviceId && device.PolicyVersion >= Settings.PolicyVersion,
            cancellationToken);

    /// <summary>
    /// Records that the engineer accepted the policy on this phone, and turns tracking on for every project where
    /// they are a site engineer. Saved here.
    /// </summary>
    public async Task<LocationDevice> AcceptPolicyAsync(int userId, string deviceId, string platform, string? appVersion, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        for (var attempt = 0; ; attempt++)
        {
            var device = await db.LocationDevices.FirstOrDefaultAsync(row => row.UserId == userId && row.DeviceId == deviceId, cancellationToken);
            if (device is null)
            {
                device = new LocationDevice { UserId = userId, DeviceId = deviceId };
                db.LocationDevices.Add(device);
            }

            device.Platform = platform;
            device.AppVersion = appVersion;
            if (device.PolicyVersion < Settings.PolicyVersion)
            {
                device.PolicyVersion = Settings.PolicyVersion;
                device.PolicyAcceptedAt = now;
            }

            await TurnOnAsync(userId, await TrackedProjectsAsync(userId, cancellationToken), now, cancellationToken);
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
    public async Task<TrackingBatchResult?> RecordAsync(int userId, LocationDevice device, TrackingBatchRequest request, ValidationErrors errors, CancellationToken cancellationToken)
    {
        var settings = Settings;
        var now = clock.UtcNow;
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

        var oldest = now.AddDays(-settings.MaxLateDays);
        var rejected = new List<RejectedPoint>();
        var valid = new List<(Guid Id, DateTime RecordedAt, decimal Latitude, decimal Longitude, int? Accuracy)>();
        var seen = new HashSet<Guid>();
        foreach (var point in points)
        {
            if (Reject(point, now, oldest) is { } reason)
            {
                rejected.Add(new RejectedPoint(point?.Id, reason));
                continue;
            }

            // With all-day tracking switched off (a policy choice), positions outside working hours are not kept,
            // like the old single-position endpoint: the phone deletes them and shows why.
            if (!SiteLocationRules.InWorkingHours(point!.RecordedAt!.Value.ToUniversalTime(), clock.Zone, settings))
            {
                rejected.Add(new RejectedPoint(point.Id, "outsideHours"));
                continue;
            }

            var id = Guid.Parse(point.Id!.Trim());
            if (seen.Add(id))
            {
                valid.Add((id, point.RecordedAt!.Value.ToUniversalTime(), Math.Round(point.Latitude!.Value, 6), Math.Round(point.Longitude!.Value, 6), point.AccuracyMeters));
            }
        }

        var beats = heartbeats
            .Where(beat => beat?.RecordedAt is { } at && at.ToUniversalTime() <= now + MaxClockSkew && at.ToUniversalTime() >= oldest)
            .Select(beat => beat!)
            .OrderBy(beat => beat.RecordedAt!.Value.ToUniversalTime())
            .ToList();

        var projects = await TrackedProjectsAsync(userId, cancellationToken);
        var ids = valid.Select(point => point.Id).ToList();
        int duplicates;

        for (var attempt = 0; ; attempt++)
        {
            // The sharings as tracked here, including any this upload just turned on (a query would miss those).
            var sharings = await TurnOnAsync(userId, projects, now, cancellationToken);

            var known = ids.Count == 0
                ? []
                : await db.LocationPings.AsNoTracking()
                    .Where(ping => ping.UserId == userId && ping.ClientPointId != null && ids.Contains(ping.ClientPointId.Value))
                    .Select(ping => ping.ClientPointId!.Value)
                    .Distinct()
                    .ToListAsync(cancellationToken);
            var fresh = valid.Where(point => !known.Contains(point.Id)).OrderBy(point => point.RecordedAt).ToList();
            duplicates = valid.Count - fresh.Count;

            // Each alert change is its own record and its own notification. When one upload changes the same alert a
            // second time (left and came back, or came back and left again), the first change is saved before the
            // second is made: opening and closing look the alert up in the database.
            var changedAlerts = new HashSet<(int ProjectId, string Kind)>();
            var lost = false;
            async Task<bool> ReadyToChangeAsync(int projectId, string kind)
            {
                if (changedAlerts.Add((projectId, kind)))
                {
                    return true;
                }

                changedAlerts.Clear();
                changedAlerts.Add((projectId, kind));
                return await TrySaveAsync(cancellationToken);
            }

            foreach (var point in fresh)
            {
                var inside = projects.ToDictionary(
                    project => project.ProjectId,
                    project => project is { SiteLatitude: { } lat, SiteLongitude: { } lng }
                        ? SiteLocationRules.InsideSite(SiteLocationRules.DistanceMeters(lat, lng, point.Latitude, point.Longitude), point.Accuracy, project.SiteRadiusMeters)
                        : null);

                foreach (var sharing in sharings)
                {
                    var project = projects.First(candidate => candidate.ProjectId == sharing.ProjectId);
                    int? distance = project is { SiteLatitude: { } siteLat, SiteLongitude: { } siteLng }
                        ? SiteLocationRules.DistanceMeters(siteLat, siteLng, point.Latitude, point.Longitude)
                        : null;
                    db.LocationPings.Add(new LocationPing
                    {
                        ProjectId = sharing.ProjectId,
                        UserId = userId,
                        RecordedAt = point.RecordedAt,
                        ReceivedAt = now,
                        Latitude = point.Latitude,
                        Longitude = point.Longitude,
                        AccuracyMeters = point.Accuracy,
                        DistanceMeters = distance,
                        InsideSite = inside[sharing.ProjectId],
                        ClientPointId = point.Id,
                    });

                    // Only a newer position moves the current state; an older one sent late stays in the history.
                    if (sharing.LastPingAt is null || point.RecordedAt >= sharing.LastPingAt)
                    {
                        sharing.LastPingAt = point.RecordedAt;
                        sharing.LastLatitude = point.Latitude;
                        sharing.LastLongitude = point.Longitude;
                        sharing.LastAccuracyMeters = point.Accuracy;
                        sharing.LastDistanceMeters = distance;
                    }

                    if (!SiteLocationRules.IsLive(point.RecordedAt, now, settings))
                    {
                        continue;
                    }

                    if (sharing.UnavailableSince is not null)
                    {
                        if (!await ReadyToChangeAsync(sharing.ProjectId, nameof(LocationUnavailableAlert)))
                        {
                            lost = true;
                            break;
                        }

                        // An outage is the server hearing nothing; it ends when positions arrive again (now), not at
                        // the capture time of a queued position, which can be earlier than the moment it was detected.
                        sharing.UnavailableSince = null;
                        await alerts.CloseAsync<LocationUnavailableAlert>(sharing.ProjectId, userId, LocationAlertStatus.Recovered, now, cancellationToken);
                        log.LocationBack(userId, sharing.ProjectId);
                    }

                    var insideAnother = inside.Any(entry => entry.Key != sharing.ProjectId && entry.Value == true);
                    if (SiteLocationRules.CountsAsOutside(inside[sharing.ProjectId], insideAnother))
                    {
                        sharing.OutsidePings++;
                        if (sharing.OutsideSince is null && sharing.OutsidePings >= settings.OutsidePingsBeforeAlert)
                        {
                            if (!await ReadyToChangeAsync(sharing.ProjectId, nameof(OutsideSiteAlert)))
                            {
                                lost = true;
                                break;
                            }

                            sharing.OutsideSince = point.RecordedAt;
                            var told = await alerts.OpenAsync<OutsideSiteAlert>(sharing.ProjectId, userId, point.RecordedAt, cancellationToken);
                            log.OutsideSite(userId, sharing.ProjectId, told);
                        }
                    }
                    else if (inside[sharing.ProjectId] == true || insideAnother)
                    {
                        sharing.OutsidePings = 0;
                        if (sharing.OutsideSince is not null)
                        {
                            if (!await ReadyToChangeAsync(sharing.ProjectId, nameof(OutsideSiteAlert)))
                            {
                                lost = true;
                                break;
                            }

                            sharing.OutsideSince = null;
                            await alerts.CloseAsync<OutsideSiteAlert>(sharing.ProjectId, userId, LocationAlertStatus.Recovered, point.RecordedAt, cancellationToken);
                            log.BackInsideSite(userId, sharing.ProjectId);
                        }
                    }
                }

                if (lost)
                {
                    break;
                }
            }

            if (!lost)
            {
                await ApplyHeartbeatsAsync(userId, device, request.AppVersion, beats, sharings, now, cancellationToken);
            }

            if (!lost && await TrySaveAsync(cancellationToken))
            {
                log.TrackingBatch(userId, fresh.Count, duplicates, rejected.Count, beats.Count);
                return new TrackingBatchResult(fresh.Count, duplicates, rejected, sharings.Count, settings.HeartbeatMinutes * 60, now);
            }

            if (attempt > 0)
            {
                throw new DbUpdateConcurrencyException("The engineer's tracking rows kept changing.");
            }

            // Lost a race with the monitor or a parallel upload: the tracker was cleared; reload the device and try once more.
            device = await db.LocationDevices.FirstAsync(row => row.Id == device.Id, cancellationToken);
        }
    }

    /// <summary>The latest heartbeat updates the phone's row; a live one may open or close the battery alert.</summary>
    private async Task ApplyHeartbeatsAsync(
        int userId, LocationDevice device, string? appVersion, List<HeartbeatRequest> beats, List<LocationSharing> sharings, DateTime now, CancellationToken cancellationToken)
    {
        if (Input.Clean(appVersion) is { } version)
        {
            device.AppVersion = version.Length > LocationDeviceConfiguration.AppVersionMaxLength
                ? version[..LocationDeviceConfiguration.AppVersionMaxLength]
                : version;
        }

        var latest = beats.LastOrDefault();
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
        device.TrackingState = LocationDeviceConfiguration.TrackingStates.Contains(latest.TrackingState) ? latest.TrackingState : null;
        device.Permission = LocationDeviceConfiguration.Permissions.Contains(latest.Permission) ? latest.Permission : null;
        device.GpsEnabled = latest.GpsEnabled;
        device.PendingPoints = latest.PendingPoints is >= 0 ? latest.PendingPoints : null;

        if (!SiteLocationRules.IsLive(recordedAt, now, Settings))
        {
            return;
        }

        if (device.LowBatterySince is null && SiteLocationRules.IsLowBattery(device.BatteryPercent, device.BatteryCharging, Settings))
        {
            device.LowBatterySince = recordedAt;
            foreach (var sharing in sharings)
            {
                var told = await alerts.OpenAsync<LowBatteryAlert>(sharing.ProjectId, userId, recordedAt, cancellationToken);
                log.LowBattery(userId, sharing.ProjectId, told);
            }
        }
        else if (device.LowBatterySince is not null && SiteLocationRules.IsBatteryRecovered(device.BatteryPercent, device.BatteryCharging, Settings))
        {
            device.LowBatterySince = null;
            foreach (var sharing in sharings)
            {
                await alerts.CloseAsync<LowBatteryAlert>(sharing.ProjectId, userId, LocationAlertStatus.Recovered, recordedAt, cancellationToken);
            }

            log.BatteryRecovered(userId);
        }
    }

    /// <summary>
    /// Tracking is on for every project where the engineer is a site engineer (added to the tracker, saved by the
    /// caller). Returns those sharings, as tracked.
    /// </summary>
    private async Task<List<LocationSharing>> TurnOnAsync(int userId, IReadOnlyList<TrackedProject> projects, DateTime now, CancellationToken cancellationToken)
    {
        var sharings = new List<LocationSharing>();
        if (projects.Count == 0)
        {
            return sharings;
        }

        var projectIds = projects.Select(project => project.ProjectId).ToList();
        var existing = await db.LocationSharings
            .Where(sharing => sharing.UserId == userId && projectIds.Contains(sharing.ProjectId))
            .ToListAsync(cancellationToken);

        foreach (var projectId in projectIds)
        {
            var sharing = existing.FirstOrDefault(row => row.ProjectId == projectId);
            if (sharing is null)
            {
                sharing = new LocationSharing { ProjectId = projectId, UserId = userId };
                db.LocationSharings.Add(sharing);
            }

            if (!sharing.IsOn)
            {
                sharing.IsOn = true;
                sharing.TurnedOnAt = now;
                sharing.TurnedOffAt = null;
                sharing.UnavailableSince = null;
                sharing.OutsideSince = null;
                sharing.OutsidePings = 0;
                log.LocationSharingOn(userId, projectId);
            }

            sharings.Add(sharing);
        }

        return sharings;
    }

    /// <summary>Why one queued position is refused for good, or <c>null</c> when it is fine.</summary>
    private static string? Reject(TrackedPointRequest? point, DateTime now, DateTime oldest)
    {
        if (point is null || !Guid.TryParse(point.Id?.Trim(), out var id) || id == Guid.Empty)
        {
            return "id";
        }

        if (point.Latitude is not { } latitude || latitude is < -90 or > 90 || point.Longitude is not { } longitude || longitude is < -180 or > 180)
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

    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Lost a race: a row changed under us, or the same position, row or alert was added at the same moment.
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
                                                   || SqlViolations.IsUnique(exception, LocationPingConfiguration.ClientPointIndex)
                                                   || SqlViolations.IsUnique(exception, LocationDeviceConfiguration.UserDeviceIndex)
                                                   || SqlViolations.IsUnique(exception, "UX_LocationSharing_Project_User")
                                                   || SqlViolations.IsUnique(exception, "UX_LocationAlert_Open"))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
