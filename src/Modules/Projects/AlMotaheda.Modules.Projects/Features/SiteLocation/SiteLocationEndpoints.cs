using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Features.SiteLocation;

/// <summary>What one site engineer sees about their own sharing on a project.</summary>
/// <param name="CanShare">True for an active site engineer of a project that isn't completed.</param>
internal sealed record MySharingView(
    bool CanShare,
    bool IsOn,
    DateTime? TurnedOnAt,
    DateTime? LastPingAt,
    int HeartbeatMinutes,
    IReadOnlyList<string> WorkDays,
    string WorkStart,
    string WorkEnd,
    bool InWorkingHours);

/// <summary>
/// What the engineer's phone last reported about itself (automatic tracking). Only what it measured, with the time it
/// measured it; empty when no phone accepted the tracking policy.
/// </summary>
internal sealed record EngineerDeviceView(
    DateTime PolicyAcceptedAt,
    DateTime? LastHeartbeatAt,
    int? BatteryPercent,
    bool? BatteryCharging,
    string? TrackingState,
    string? Permission,
    bool? GpsEnabled,
    int? PendingPoints,
    DateTime? LowBatterySince,
    string Platform,
    string? AppVersion);

/// <summary>One site engineer as the project manager or an Executive sees them.</summary>
/// <param name="State">
/// <c>Off</c> (never turned on, or turned off), <c>Unavailable</c> (no position for too long during working hours),
/// <c>OutsideSite</c>, <c>OnSite</c>, or <c>Sharing</c> (on, but no position yet or too rough to tell).
/// </param>
internal sealed record EngineerLocationView(
    int UserId,
    string NameAr,
    string NameEn,
    string State,
    bool IsOn,
    DateTime? TurnedOnAt,
    DateTime? TurnedOffAt,
    DateTime? LastPingAt,
    decimal? Latitude,
    decimal? Longitude,
    int? AccuracyMeters,
    int? DistanceMeters,
    bool? InsideSite,
    DateTime? UnavailableSince,
    DateTime? OutsideSince,
    EngineerDeviceView? Device = null);

/// <summary>One alert episode of the project (one outage, one time outside, one low battery), once for all recipients.</summary>
internal sealed record LocationAlertView(string Kind, int EngineerUserId, string Status, DateTime StartedAt, DateTime? EndedAt);

internal sealed record SiteLocationView(
    decimal? SiteLatitude,
    decimal? SiteLongitude,
    int SiteRadiusMeters,
    int HeartbeatMinutes,
    int UnavailableAfterMinutes,
    bool InWorkingHours,
    IReadOnlyList<EngineerLocationView> Items);

internal sealed record PositionView(DateTime RecordedAt, decimal Latitude, decimal Longitude, int? AccuracyMeters, int? DistanceMeters, bool? InsideSite);

/// <summary>
/// The site engineers' location sharing (owner's rules of 2026-10-17):
/// <list type="bullet">
/// <item><c>GET /api/projects/{pid}/site-location/me</c>, <c>PUT …/me</c> <c>{isOn}</c>: the engineer's own switch.</item>
/// <item><c>POST …/me/positions</c> <c>{latitude, longitude, accuracyMeters, recordedAt}</c>: the app's heartbeat.</item>
/// <item><c>GET …/site-location</c> and <c>GET …/site-location/{userId}/positions?date=</c>: project manager and Executives only.</item>
/// </list>
/// </summary>
internal static class SiteLocationEndpoints
{
    internal sealed record SharingRequest(bool? IsOn);

    internal sealed record PositionRequest(decimal? Latitude, decimal? Longitude, int? AccuracyMeters, DateTime? RecordedAt);

    /// <param name="Stored">False outside working hours: nothing is kept then.</param>
    /// <param name="NextInSeconds">When the app should send the next position.</param>
    internal sealed record PositionResult(bool Stored, int? DistanceMeters, bool? InsideSite, int NextInSeconds);

    internal sealed record PositionsResponse(IReadOnlyList<PositionView> Items);

    internal sealed record AlertsResponse(IReadOnlyList<LocationAlertView> Items);

    /// <summary>A position may be up to this old: the app sends what it measured, a little late on a bad connection.</summary>
    private static readonly TimeSpan MaxPositionAge = TimeSpan.FromHours(1);

    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(2);

    private const int MaxAccuracyMeters = 100_000;

    public static void Map(IEndpointRouteBuilder projects)
    {
        projects.MapGet("/{projectId:int}/site-location/me", GetMineAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("GetMyLocationSharing")
            .WithSummary("A site engineer's own location sharing on a project, and the working hours");

        projects.MapPut("/{projectId:int}/site-location/me", SetMineAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("SetMyLocationSharing")
            .WithSummary("Turn location sharing on or off (the project's site engineers only)");

        projects.MapPost("/{projectId:int}/site-location/me/positions", RecordPositionAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("RecordMyPosition")
            .WithSummary("The app's heartbeat: one position while sharing is on");

        projects.MapGet("/{projectId:int}/site-location", GetTeamAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("GetSiteLocation")
            .WithSummary("Where the project's site engineers are (the project manager and Executives only)");

        projects.MapGet("/{projectId:int}/site-location/alerts", GetAlertsAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("ListLocationAlerts")
            .WithSummary("The project's location alerts of the last days: outages, outside the site, low battery (the project manager and Executives only)");

        projects.MapGet("/{projectId:int}/site-location/{userId:int}/positions", GetPositionsAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("ListEngineerPositions")
            .WithSummary("One engineer's positions on one company day (the project manager and Executives only)");
    }

    private sealed record ProjectSite(ProjectStatus Status, decimal? Latitude, decimal? Longitude, int RadiusMeters);

    private static Task<ProjectSite?> FindSiteAsync(ProjectAccess access, ProjectCaller caller, int projectId, CancellationToken cancellationToken) =>
        access.VisibleProjects(caller)
            .Where(project => project.Id == projectId)
            .Select(project => new ProjectSite(project.Status, project.SiteLatitude, project.SiteLongitude, project.SiteRadiusMeters))
            .FirstOrDefaultAsync(cancellationToken);

    internal static async Task<Results<Ok<MySharingView>, ProblemHttpResult>> GetMineAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        CompanyClock clock,
        IOptions<SiteLocationOptions> options,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await FindSiteAsync(access, caller, projectId, cancellationToken) is not { } site)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var responsibility = await access.ResponsibilityAsync(caller.UserId, projectId, cancellationToken);
        var sharing = await db.LocationSharings.AsNoTracking()
            .FirstOrDefaultAsync(row => row.ProjectId == projectId && row.UserId == caller.UserId, cancellationToken);
        return TypedResults.Ok(Mine(responsibility, site, sharing, clock, options.Value));
    }

    internal static async Task<Results<Ok<MySharingView>, ValidationProblem, ProblemHttpResult>> SetMineAsync(
        int projectId,
        SharingRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        SiteLocationAlerts alerts,
        CompanyClock clock,
        IOptions<SiteLocationOptions> options,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (request.IsOn is not { } isOn)
        {
            var errors = new ValidationErrors();
            errors.Add("isOn", "isOn is required.");
            return ProjectProblems.Validation(errors);
        }

        if (await FindSiteAsync(access, caller, projectId, cancellationToken) is not { } site)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var responsibility = await access.ResponsibilityAsync(caller.UserId, projectId, cancellationToken);
        var sharing = await db.LocationSharings.FirstOrDefaultAsync(row => row.ProjectId == projectId && row.UserId == caller.UserId, cancellationToken);

        // Turning off works for the old opt-in sharing; once the engineer accepted the automatic tracking policy on a
        // phone, that phone's uploads turn sharing back on, so the switch would only pretend: it is refused instead.
        // Turning on needs an active site engineer.
        var settings = options.Value;
        if (!isOn
            && sharing is { IsOn: true }
            && await db.LocationDevices.AnyAsync(device => device.UserId == caller.UserId && device.PolicyVersion >= settings.PolicyVersion, cancellationToken))
        {
            return ProjectProblems.TrackingFollowsPolicy();
        }

        if (isOn)
        {
            if (responsibility != MemberResponsibility.SiteEngineer)
            {
                return ProjectProblems.NotSiteEngineer();
            }

            if (site.Status == ProjectStatus.Completed)
            {
                return ProjectProblems.CompletedProject();
            }
        }

        var now = clock.UtcNow;
        if (isOn && sharing is not { IsOn: true })
        {
            if (sharing is null)
            {
                sharing = new LocationSharing { ProjectId = projectId, UserId = caller.UserId };
                db.LocationSharings.Add(sharing);
            }

            sharing.IsOn = true;
            sharing.TurnedOnAt = now;
            sharing.TurnedOffAt = null;
            sharing.UnavailableSince = null;
            sharing.OutsideSince = null;
            sharing.OutsidePings = 0;
            if (!await TrySaveAsync(db, cancellationToken))
            {
                return ApiProblem.ConcurrencyConflictResult();
            }

            log.LocationSharingOn(caller.UserId, projectId);
        }
        else if (!isOn && sharing is { IsOn: true })
        {
            await alerts.TurnOffAsync(sharing, now, cancellationToken);
            if (!await TrySaveAsync(db, cancellationToken))
            {
                return ApiProblem.ConcurrencyConflictResult();
            }

            log.LocationSharingOff(caller.UserId, projectId);
        }

        return TypedResults.Ok(Mine(responsibility, site, sharing, clock, options.Value));
    }

    internal static async Task<Results<Ok<PositionResult>, ValidationProblem, ProblemHttpResult>> RecordPositionAsync(
        int projectId,
        PositionRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        SiteLocationAlerts alerts,
        CompanyClock clock,
        IOptions<SiteLocationOptions> options,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var now = clock.UtcNow;
        var errors = new ValidationErrors();
        if (request.Latitude is not { } latitude || latitude is < -90 or > 90)
        {
            errors.Add("latitude", "latitude must be between -90 and 90.");
        }

        if (request.Longitude is not { } longitude || longitude is < -180 or > 180)
        {
            errors.Add("longitude", "longitude must be between -180 and 180.");
        }

        if (request.AccuracyMeters is < 0 or > MaxAccuracyMeters)
        {
            errors.Add("accuracyMeters", $"accuracyMeters must be 0 to {MaxAccuracyMeters}.");
        }

        var recordedAt = request.RecordedAt?.ToUniversalTime() ?? now;
        if (recordedAt > now + MaxClockSkew || recordedAt < now - MaxPositionAge)
        {
            errors.Add("recordedAt", "recordedAt must be within the last hour.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await FindSiteAsync(access, caller, projectId, cancellationToken) is not { } site)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (await access.ResponsibilityAsync(caller.UserId, projectId, cancellationToken) != MemberResponsibility.SiteEngineer)
        {
            return ProjectProblems.NotSiteEngineer();
        }

        if (site.Status == ProjectStatus.Completed)
        {
            return ProjectProblems.CompletedProject();
        }

        var settings = options.Value;
        var next = settings.HeartbeatMinutes * 60;
        var lat = Math.Round(request.Latitude!.Value, 6);
        var lng = Math.Round(request.Longitude!.Value, 6);
        int? distance = site is { Latitude: { } siteLat, Longitude: { } siteLng }
            ? SiteLocationRules.DistanceMeters(siteLat, siteLng, lat, lng)
            : null;
        var inside = distance is { } meters ? SiteLocationRules.InsideSite(meters, request.AccuracyMeters, site.RadiusMeters) : null;

        // The monitor may change the same row at the same moment: one retry on a fresh copy.
        for (var attempt = 0; ; attempt++)
        {
            var sharing = await db.LocationSharings.FirstOrDefaultAsync(row => row.ProjectId == projectId && row.UserId == caller.UserId, cancellationToken);
            if (sharing is not { IsOn: true })
            {
                return ProjectProblems.SharingOff();
            }

            // Outside working hours nothing is kept, as agreed.
            if (!SiteLocationRules.InWorkingHours(now, clock.Zone, settings))
            {
                return TypedResults.Ok(new PositionResult(false, null, null, next));
            }

            db.LocationPings.Add(new LocationPing
            {
                ProjectId = projectId,
                UserId = caller.UserId,
                RecordedAt = recordedAt,
                ReceivedAt = now,
                Latitude = lat,
                Longitude = lng,
                AccuracyMeters = request.AccuracyMeters,
                DistanceMeters = distance,
                InsideSite = inside,
            });

            var back = false;
            var outside = 0;
            var backInside = false;

            // An older position sent late is kept for the trail but doesn't move the current state.
            if (sharing.LastPingAt is null || recordedAt >= sharing.LastPingAt)
            {
                sharing.LastPingAt = recordedAt;
                sharing.LastLatitude = lat;
                sharing.LastLongitude = lng;
                sharing.LastAccuracyMeters = request.AccuracyMeters;
                sharing.LastDistanceMeters = distance;

                if (sharing.UnavailableSince is not null)
                {
                    sharing.UnavailableSince = null;
                    await alerts.CloseAsync<LocationUnavailableAlert>(projectId, caller.UserId, LocationAlertStatus.Recovered, now, cancellationToken);
                    back = true;
                }

                if (inside == false)
                {
                    sharing.OutsidePings++;
                    if (sharing.OutsideSince is null && sharing.OutsidePings >= settings.OutsidePingsBeforeAlert)
                    {
                        sharing.OutsideSince = now;
                        outside = await alerts.OpenAsync<OutsideSiteAlert>(projectId, caller.UserId, now, cancellationToken);
                    }
                }
                else if (inside == true)
                {
                    sharing.OutsidePings = 0;
                    if (sharing.OutsideSince is not null)
                    {
                        sharing.OutsideSince = null;
                        await alerts.CloseAsync<OutsideSiteAlert>(projectId, caller.UserId, LocationAlertStatus.Recovered, now, cancellationToken);
                        backInside = true;
                    }
                }
            }

            if (!await TrySaveAsync(db, cancellationToken))
            {
                if (attempt == 0)
                {
                    continue;
                }

                return ApiProblem.ConcurrencyConflictResult();
            }

            if (back)
            {
                log.LocationBack(caller.UserId, projectId);
            }

            if (sharing.OutsideSince == now)
            {
                log.OutsideSite(caller.UserId, projectId, outside);
            }

            if (backInside)
            {
                log.BackInsideSite(caller.UserId, projectId);
            }

            return TypedResults.Ok(new PositionResult(true, distance, inside, next));
        }
    }

    internal static async Task<Results<Ok<SiteLocationView>, ProblemHttpResult>> GetTeamAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        IOptions<SiteLocationOptions> options,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await FindSiteAsync(access, caller, projectId, cancellationToken) is not { } site)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (!await MaySeeLocationsAsync(access, caller, projectId, cancellationToken))
        {
            return ProjectProblems.LocationsNotAllowed();
        }

        var engineers = await db.ProjectMembers.AsNoTracking()
            .Where(member => member.ProjectId == projectId && member.EndDate == null && member.Responsibility == MemberResponsibility.SiteEngineer)
            .Select(member => member.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var sharings = await db.LocationSharings.AsNoTracking()
            .Where(row => row.ProjectId == projectId && engineers.Contains(row.UserId))
            .ToDictionaryAsync(row => row.UserId, cancellationToken);
        var people = await users.FindAsync(engineers, cancellationToken);

        // The phone that reported last (an engineer may have changed phones).
        var devices = (await db.LocationDevices.AsNoTracking()
                .Where(device => engineers.Contains(device.UserId))
                .ToListAsync(cancellationToken))
            .GroupBy(device => device.UserId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(device => device.LastHeartbeatAt ?? device.PolicyAcceptedAt).First());

        var items = engineers
            .Select(userId =>
            {
                sharings.TryGetValue(userId, out var sharing);
                people.TryGetValue(userId, out var person);
                devices.TryGetValue(userId, out var device);
                return Engineer(userId, person, sharing, site.RadiusMeters) with { Device = DeviceOf(device) };
            })
            .OrderBy(item => item.NameEn, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.UserId)
            .ToList();

        var settings = options.Value;
        return TypedResults.Ok(new SiteLocationView(
            site.Latitude,
            site.Longitude,
            site.RadiusMeters,
            settings.HeartbeatMinutes,
            settings.UnavailableAfterMinutes,
            SiteLocationRules.InWorkingHours(clock.UtcNow, clock.Zone, settings),
            items));
    }

    internal static async Task<Results<Ok<PositionsResponse>, ValidationProblem, ProblemHttpResult>> GetPositionsAsync(
        int projectId,
        int userId,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        CompanyClock clock,
        CancellationToken cancellationToken,
        string? date = null)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var day = Input.OptionalDate(date, errors, "date", "The date") ?? clock.Today;
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await FindSiteAsync(access, caller, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (!await MaySeeLocationsAsync(access, caller, projectId, cancellationToken))
        {
            return ProjectProblems.LocationsNotAllowed();
        }

        var from = clock.StartOfDayUtc(day);
        var to = clock.StartOfDayUtc(day.AddDays(1));
        var items = await db.LocationPings.AsNoTracking()
            .Where(ping => ping.ProjectId == projectId && ping.UserId == userId && ping.RecordedAt >= from && ping.RecordedAt < to)
            .OrderBy(ping => ping.RecordedAt)
            .ThenBy(ping => ping.Id)
            .Take(5_000)
            .Select(ping => new PositionView(ping.RecordedAt, ping.Latitude, ping.Longitude, ping.AccuracyMeters, ping.DistanceMeters, ping.InsideSite))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new PositionsResponse(items));
    }

    internal static async Task<Results<Ok<AlertsResponse>, ValidationProblem, ProblemHttpResult>> GetAlertsAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        CompanyClock clock,
        CancellationToken cancellationToken,
        int? days = null)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (days is < 1 or > 90)
        {
            var errors = new ValidationErrors();
            errors.Add("days", "days must be 1 to 90.");
            return ProjectProblems.Validation(errors);
        }

        if (await FindSiteAsync(access, caller, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (!await MaySeeLocationsAsync(access, caller, projectId, cancellationToken))
        {
            return ProjectProblems.LocationsNotAllowed();
        }

        var from = clock.UtcNow.AddDays(-(days ?? 7));
        var rows = await db.LocationAlerts.AsNoTracking()
            .Where(alert => alert.ProjectId == projectId && alert.StartedAt >= from)
            .Select(alert => new
            {
                Kind = EF.Property<string>(alert, Data.Configurations.LocationAlertConfiguration.KindColumn),
                alert.EngineerUserId,
                alert.Status,
                alert.StartedAt,
                alert.EndedAt,
            })
            .Take(2_000)
            .ToListAsync(cancellationToken);

        // One row per recipient in the table; one episode on the screen.
        var items = rows
            .GroupBy(row => (row.Kind, row.EngineerUserId, row.StartedAt))
            .Select(group => group.OrderBy(row => row.Status == LocationAlertStatus.Open ? 0 : 1).First())
            .OrderByDescending(row => row.StartedAt)
            .Select(row => new LocationAlertView(row.Kind, row.EngineerUserId, row.Status.ToString(), row.StartedAt, row.EndedAt))
            .ToList();
        return TypedResults.Ok(new AlertsResponse(items));
    }

    private static EngineerDeviceView? DeviceOf(LocationDevice? device) =>
        device is null
            ? null
            : new EngineerDeviceView(
                device.PolicyAcceptedAt,
                device.LastHeartbeatAt,
                device.BatteryPercent,
                device.BatteryCharging,
                device.TrackingState,
                device.Permission,
                device.GpsEnabled,
                device.PendingPoints,
                device.LowBatterySince,
                device.Platform,
                device.AppVersion);

    /// <summary>Only Executives and the project's active manager see where the engineers are.</summary>
    private static async Task<bool> MaySeeLocationsAsync(ProjectAccess access, ProjectCaller caller, int projectId, CancellationToken cancellationToken) =>
        caller.IsExecutive
        || await access.ResponsibilityAsync(caller.UserId, projectId, cancellationToken) == MemberResponsibility.ProjectManager;

    private static MySharingView Mine(
        MemberResponsibility? responsibility, ProjectSite site, LocationSharing? sharing, CompanyClock clock, SiteLocationOptions settings) =>
        new(
            responsibility == MemberResponsibility.SiteEngineer && site.Status != ProjectStatus.Completed,
            sharing?.IsOn == true,
            sharing?.IsOn == true ? sharing.TurnedOnAt : null,
            sharing?.LastPingAt,
            settings.HeartbeatMinutes,
            settings.WorkDays.Select(day => day.ToString()).ToList(),
            settings.WorkStart,
            settings.WorkEnd,
            SiteLocationRules.InWorkingHours(clock.UtcNow, clock.Zone, settings));

    private static EngineerLocationView Engineer(int userId, UserSummary? person, LocationSharing? sharing, int radiusMeters)
    {
        var isOn = sharing?.IsOn == true;
        bool? inside = sharing?.LastDistanceMeters is { } distance
            ? SiteLocationRules.InsideSite(distance, sharing.LastAccuracyMeters, radiusMeters)
            : null;
        var state = !isOn ? "Off"
            : sharing!.UnavailableSince is not null ? "Unavailable"
            : sharing.OutsideSince is not null ? "OutsideSite"
            : inside == true ? "OnSite"
            : "Sharing";

        return new EngineerLocationView(
            userId,
            person?.NameAr ?? "",
            person?.NameEn ?? "",
            state,
            isOn,
            sharing?.TurnedOnAt,
            sharing?.TurnedOffAt,
            sharing?.LastPingAt,
            sharing?.LastLatitude,
            sharing?.LastLongitude,
            sharing?.LastAccuracyMeters,
            sharing?.LastDistanceMeters,
            inside,
            sharing?.UnavailableSince,
            sharing?.OutsideSince);
    }

    private static async Task<bool> TrySaveAsync(ProjectsDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Lost a race: the row changed under us, or the same row or alert was added at the same moment.
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
                                                   || SqlViolations.IsUnique(exception, "UX_LocationSharing_Project_User"))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
