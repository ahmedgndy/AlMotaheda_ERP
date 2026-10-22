using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.HR.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Features.Gps;

/// <summary>A stored position as the monitoring screens show it.</summary>
internal sealed record GpsPositionView(DateTime RecordedAt, DateTime? ReceivedAt, decimal Latitude, decimal Longitude, int? AccuracyMeters, decimal? SpeedKmh);

/// <summary>One vehicle in the monitoring centre: its latest known position (with its age), its tracking health and its motion.</summary>
/// <param name="Motion">What the recent positions say (<c>Moving</c>, <c>SuspectedStop</c>, <c>Stopped</c>), or <c>Unknown</c> once the last position is not live: an old position never says what the vehicle is doing now.</param>
internal sealed record FleetVehicleStatusView(
    int VehicleId,
    string AssetNo,
    string NameAr,
    string NameEn,
    string Plate,
    string Type,
    string Status,
    string FuelType,
    decimal OdometerKm,
    bool TrackingEnabled,
    DriverRef? Driver,
    int? AssignmentId,
    GpsPositionView? LastPosition,
    DriverRef? LastPositionDriver,
    GpsHealthView Health,
    GpsMotion Motion,
    GpsDeviceView? Device,
    DateTime? SilentSince,
    string RowVersion);

internal sealed record GpsLatLng(double Latitude, double Longitude);

internal sealed record GpsRoutePointView(double Latitude, double Longitude, DateTime At, int? AccuracyMeters, double? SpeedKmh);

internal sealed record GpsTripView(
    int Index,
    DateTime StartedAt,
    DateTime EndedAt,
    GpsRoutePointView Start,
    GpsRoutePointView End,
    double DistanceKm,
    double UnmeasuredKm,
    int MovingMinutes,
    double? AverageSpeedKmh,
    double? MaxSpeedKmh,
    int Samples,
    int Gaps,
    bool StartsBeforeDay,
    bool EndsAfterDay,
    bool StartedAtDataStart,
    bool Ongoing,
    DriverRef? Driver,
    IReadOnlyList<IReadOnlyList<GpsRoutePointView>> Segments);

internal sealed record GpsStopView(DateTime StartedAt, DateTime EndedAt, int Minutes, double Latitude, double Longitude, int Samples, bool HasDataGap, bool Ongoing, bool StartsBeforeDay, bool EndsAfterDay);

internal sealed record GpsGapView(DateTime From, DateTime To, int Minutes, double StraightLineKm, GpsLatLng Before, GpsLatLng After);

/// <param name="DistanceKm">Measured along accepted positions taken that day (estimated, not map-matched).</param>
internal sealed record GpsDaySummaryView(
    double DistanceKm, int MovingMinutes, int StoppedMinutes, int Trips, int Stops, int Gaps, int Samples, int Accepted, int Inaccurate, int Implausible, int Duplicates,
    DateTime? FirstPointAt, DateTime? LastPointAt, string DistanceMethod);

/// <param name="DataStatus"><c>Recorded</c>; <c>NoPositions</c> (nothing stored for the day); <c>NoUsablePositions</c> (only inaccurate ones).</param>
/// <param name="PendingOnDevice">Positions the driver's phone said it still holds (its last heartbeat), when it said so.</param>
internal sealed record GpsDayTimelineView(
    int VehicleId,
    string Plate,
    DateOnly Date,
    string TimeZone,
    DateTime DayStartUtc,
    DateTime DayEndUtc,
    string DataStatus,
    int? PendingOnDevice,
    DateTime? DeviceHeartbeatAt,
    GpsDaySummaryView Summary,
    IReadOnlyList<GpsTripView> Trips,
    IReadOnlyList<GpsStopView> Stops,
    IReadOnlyList<GpsGapView> Gaps,
    IReadOnlyList<DriverRef> Drivers,
    string Note);

/// <summary>Builds the monitoring views with one query per kind of row.</summary>
internal sealed class FleetMonitor(FleetDbContext db, IEmployeeDirectory employees, CompanyClock clock, IOptions<FleetOptions> options)
{
    private VehicleGpsOptions Settings => options.Value.Gps;

    public async Task<IReadOnlyList<FleetVehicleStatusView>> StatusesAsync(IReadOnlyList<Vehicle> vehicles, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var ids = vehicles.Select(vehicle => vehicle.Id).ToList();
        var assignments = await db.VehicleDriverAssignments.AsNoTracking().Where(assignment => ids.Contains(assignment.VehicleId) && assignment.EndDate == null).ToListAsync(cancellationToken);
        var states = await db.VehicleTrackingStates.AsNoTracking().Where(state => ids.Contains(state.VehicleId)).ToDictionaryAsync(state => state.VehicleId, cancellationToken);
        var employeeIds = assignments.Select(assignment => assignment.DriverEmployeeId)
            .Concat(states.Values.Select(state => state.LastDriverEmployeeId).OfType<int>()).Distinct().ToList();
        var drivers = employeeIds.Count == 0 ? new Dictionary<int, EmployeeRef>() : await employees.FindAsync(employeeIds, cancellationToken);
        var userIds = drivers.Values.Select(driver => driver.UserId).OfType<int>().Distinct().ToList();
        var policy = Settings.PolicyVersion;
        var devices = await db.VehicleTrackingDevices.AsNoTracking().Where(device => userIds.Contains(device.UserId) && device.PolicyVersion >= policy).ToListAsync(cancellationToken);

        // The motion of the vehicles whose last position is live, from their recent positions only.
        var liveIds = states.Values.Where(state => state.LastPointAt is { } at && now - at <= TimeSpan.FromMinutes(Settings.LiveMinutes)).Select(state => state.VehicleId).ToList();
        var since = now.AddMinutes(-(Settings.StopMinutes * 3 + Settings.LiveMinutes));
        var recent = liveIds.Count == 0
            ? []
            : await db.VehicleGpsPoints.AsNoTracking().Where(point => liveIds.Contains(point.VehicleId) && point.RecordedAt >= since)
                .Select(point => new { point.VehicleId, point.Id, point.RecordedAt, point.Latitude, point.Longitude, point.AccuracyMeters, point.SpeedKmh, point.DriverEmployeeId })
                .ToListAsync(cancellationToken);
        var motion = MotionSettings.From(Settings);

        return vehicles.Select(vehicle =>
        {
            var assignment = assignments.SingleOrDefault(candidate => candidate.VehicleId == vehicle.Id);
            var driver = DriverRef.From(assignment?.DriverEmployeeId, drivers);
            var userId = assignment is null ? null : drivers.GetValueOrDefault(assignment.DriverEmployeeId)?.UserId;
            var device = devices.Where(candidate => candidate.UserId == userId)
                .OrderByDescending(candidate => candidate.LastHeartbeatAt ?? candidate.PolicyAcceptedAt).FirstOrDefault();
            var state = states.GetValueOrDefault(vehicle.Id);
            var health = TrackingHealth.Classify(vehicle.TrackingEnabled, assignment is not null, device, state?.LastPointAt, now, Settings);
            var moving = liveIds.Contains(vehicle.Id)
                ? TripSegmenter.Segment(
                    recent.Where(row => row.VehicleId == vehicle.Id)
                        .Select(row => new GpsSample(row.Id, row.RecordedAt, (double)row.Latitude, (double)row.Longitude, row.AccuracyMeters, (double?)row.SpeedKmh, row.DriverEmployeeId)),
                    motion).Motion
                : new GpsMotion("Unknown", null);
            var position = state?.LastPointAt is { } at
                ? new GpsPositionView(at, state.LastReceivedAt, state.LastLatitude!.Value, state.LastLongitude!.Value, state.LastAccuracyMeters, state.LastSpeedKmh)
                : null;
            return new FleetVehicleStatusView(
                vehicle.Id, vehicle.Equipment.AssetNo, vehicle.Equipment.NameAr, vehicle.Equipment.NameEn, vehicle.Plate, vehicle.Type.ToString(), vehicle.Equipment.Status.ToString(),
                vehicle.FuelType.ToString(), vehicle.OdometerKm, vehicle.TrackingEnabled, driver, assignment?.Id, position,
                DriverRef.From(state?.LastDriverEmployeeId, drivers), health, moving, GpsDeviceView.From(device), state?.SilentSince, RowVersions.Format(vehicle.RowVersion));
        }).ToList();
    }

    /// <summary>
    /// One vehicle's company day: positions from 12 hours before to 12 hours after it are segmented, so a trip across
    /// midnight is whole; trips, stops and gaps that overlap the day are returned, flagged when they start before or
    /// end after it. The day's totals count only what happened inside it.
    /// </summary>
    public async Task<GpsDayTimelineView> DayAsync(Vehicle vehicle, DateOnly date, CancellationToken cancellationToken)
    {
        var dayStart = clock.StartOfDayUtc(date);
        var dayEnd = clock.StartOfDayUtc(date.AddDays(1));
        var windowStart = dayStart.AddHours(-12);
        var windowEnd = dayEnd.AddHours(12);
        var rows = (await db.VehicleGpsPoints.AsNoTracking()
                .Where(point => point.VehicleId == vehicle.Id && point.RecordedAt >= windowStart && point.RecordedAt < windowEnd)
                .OrderBy(point => point.RecordedAt)
                .Select(point => new { point.Id, point.RecordedAt, point.Latitude, point.Longitude, point.AccuracyMeters, point.SpeedKmh, point.DriverEmployeeId })
                .ToListAsync(cancellationToken))
            .Select(row => new GpsSample(row.Id, row.RecordedAt, (double)row.Latitude, (double)row.Longitude, row.AccuracyMeters, (double?)row.SpeedKmh, row.DriverEmployeeId))
            .ToList();
        var timeline = TripSegmenter.Segment(rows, MotionSettings.From(Settings));

        bool InDay(DateTime at) => at >= dayStart && at < dayEnd;
        bool Overlaps(DateTime start, DateTime end) => end >= dayStart && start < dayEnd;
        var gapSeconds = Settings.GapMinutes * 60.0;
        var dayRows = rows.Where(row => InDay(row.RecordedAt)).ToList();
        var dayAccepted = timeline.Accepted.Where(point => InDay(point.At)).ToList();
        var accurate = dayRows.Where(row => row.AccuracyMeters is { } accuracy && accuracy <= Settings.MaxAccuracyMeters).ToList();
        var accurateMoments = accurate.Select(row => row.RecordedAt).Distinct().Count();
        var duplicates = accurate.Count - accurateMoments;
        var implausible = Math.Max(0, accurateMoments - dayAccepted.Count);

        // The day's distance and moving time: pairs of consecutive accepted positions ending inside the day, inside a trip, not across a gap.
        double distance = 0, movingSeconds = 0;
        for (var index = 1; index < timeline.Accepted.Count; index++)
        {
            var (from, to) = (timeline.Accepted[index - 1], timeline.Accepted[index]);
            var seconds = (to.At - from.At).TotalSeconds;
            if (!InDay(to.At) || seconds > gapSeconds || !timeline.Trips.Any(trip => trip.StartedAt <= from.At && to.At <= trip.EndedAt))
            {
                continue;
            }

            distance += TripSegmenter.Meters(from.Latitude, from.Longitude, to.Latitude, to.Longitude);
            movingSeconds += seconds;
        }

        static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
        static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
        var stops = timeline.Stops.Where(stop => Overlaps(stop.StartedAt, stop.EndedAt)).ToList();
        var stoppedSeconds = stops.Sum(stop => Math.Max(0, (Min(stop.EndedAt, dayEnd) - Max(stop.StartedAt, dayStart)).TotalSeconds));
        var trips = timeline.Trips.Where(trip => Overlaps(trip.StartedAt, trip.EndedAt)).ToList();
        var gaps = timeline.Gaps.Where(gap => Overlaps(gap.Before.At, gap.After.At)).ToList();

        var driverIds = timeline.Accepted.Where(point => Overlaps(point.At, point.At)).Select(point => point.DriverEmployeeId).OfType<int>()
            .Concat(trips.SelectMany(trip => trip.Segments.SelectMany(segment => segment)).Select(point => point.DriverEmployeeId).OfType<int>())
            .Distinct().ToList();
        var drivers = driverIds.Count == 0 ? new Dictionary<int, EmployeeRef>() : await employees.FindAsync(driverIds, cancellationToken);

        static GpsRoutePointView View(GpsRoutePoint point) => new(point.Latitude, point.Longitude, point.At, point.AccuracyMeters, point.SpeedKmh);
        var tripViews = trips.Select((trip, index) =>
        {
            // The driver of a trip: the driver recorded on most of its positions (fixed at capture, never today's driver).
            var driverId = trip.Segments.SelectMany(segment => segment).Select(point => point.DriverEmployeeId).OfType<int>()
                .GroupBy(id => id).OrderByDescending(group => group.Count()).Select(group => (int?)group.Key).FirstOrDefault();
            return new GpsTripView(
                index + 1, trip.StartedAt, trip.EndedAt, View(trip.Start), View(trip.End), Math.Round(trip.DistanceMeters / 1000, 2), Math.Round(trip.UnmeasuredMeters / 1000, 2),
                (int)Math.Round(trip.MovingSeconds / 60), trip.MovingSeconds > 0 ? Math.Round(trip.DistanceMeters / trip.MovingSeconds * 3.6, 1) : null, trip.MaxSpeedKmh,
                trip.Samples, trip.Gaps, trip.StartedAt < dayStart, trip.EndedAt >= dayEnd, trip.StartedAtDataStart, trip.Ongoing, DriverRef.From(driverId, drivers),
                trip.Segments.Select(segment => (IReadOnlyList<GpsRoutePointView>)segment.Select(View).ToList()).ToList());
        }).ToList();

        var everReported = await db.VehicleTrackingStates.AsNoTracking().AnyAsync(row => row.VehicleId == vehicle.Id && row.LastPointAt != null, cancellationToken);
        var device = await DriverDeviceAsync(vehicle.Id, date, cancellationToken);
        var dataStatus = dayRows.Count == 0 ? "NoPositions" : dayAccepted.Count == 0 ? "NoUsablePositions" : "Recorded";
        var note = dataStatus switch
        {
            "NoPositions" when device?.PendingPoints is > 0 =>
                $"No positions are stored for this day yet. At {device.LastHeartbeatAt:yyyy-MM-dd HH:mm} UTC the driver's phone said it still held {device.PendingPoints} positions; they appear once it uploads them.",
            "NoPositions" when !everReported => "No positions are stored for this day. This vehicle has never reported a position.",
            "NoPositions" => "No positions are stored for this day: the vehicle was not tracked, or the phone has not uploaded them yet (it keeps them while offline).",
            "NoUsablePositions" => $"Positions exist, but none is accurate enough (better than {Settings.MaxAccuracyMeters} m) to draw a route.",
            _ => "Distances are estimated from GPS positions (great-circle between consecutive accepted positions, not map-matched). Nothing is drawn or measured across a gap.",
        };

        return new GpsDayTimelineView(
            vehicle.Id, vehicle.Plate, date, clock.Zone.Id, dayStart, dayEnd, dataStatus, device?.PendingPoints, device?.LastHeartbeatAt,
            new GpsDaySummaryView(
                Math.Round(distance / 1000, 2), (int)Math.Round(movingSeconds / 60), (int)Math.Round(stoppedSeconds / 60), trips.Count, stops.Count, gaps.Count,
                dayRows.Count, dayAccepted.Count, dayRows.Count - accurate.Count, implausible, duplicates,
                dayAccepted.FirstOrDefault()?.At, dayAccepted.LastOrDefault()?.At,
                "Sum of great-circle distances between consecutive accepted positions; not across gaps; not map-matched."),
            tripViews,
            stops.Select(stop => new GpsStopView(
                stop.StartedAt, stop.EndedAt, (int)Math.Round(stop.Seconds / 60), stop.Latitude, stop.Longitude, stop.Samples, stop.HasDataGap, stop.Ongoing,
                stop.StartedAt < dayStart, stop.EndedAt >= dayEnd)).ToList(),
            gaps.Select(gap => new GpsGapView(
                gap.Before.At, gap.After.At, (int)Math.Round(gap.Seconds / 60), Math.Round(gap.StraightLineMeters / 1000, 2),
                new GpsLatLng(gap.Before.Latitude, gap.Before.Longitude), new GpsLatLng(gap.After.Latitude, gap.After.Longitude))).ToList(),
            drivers.Keys.Order().Select(id => DriverRef.From(id, drivers)!).ToList(),
            note);
    }

    /// <summary>The phone of the driver assigned on that day (its latest heartbeat), when there is one.</summary>
    private async Task<VehicleTrackingDevice?> DriverDeviceAsync(int vehicleId, DateOnly date, CancellationToken cancellationToken)
    {
        var assignment = await db.VehicleDriverAssignments.AsNoTracking()
            .Where(row => row.VehicleId == vehicleId && row.StartDate <= date && (row.EndDate == null || row.EndDate >= date))
            .OrderByDescending(row => row.StartDate).FirstOrDefaultAsync(cancellationToken);
        if (assignment is null)
        {
            return null;
        }

        var driver = (await employees.FindAsync([assignment.DriverEmployeeId], cancellationToken)).GetValueOrDefault(assignment.DriverEmployeeId);
        if (driver?.UserId is not { } userId)
        {
            return null;
        }

        return await db.VehicleTrackingDevices.AsNoTracking().Where(device => device.UserId == userId)
            .OrderByDescending(device => device.LastHeartbeatAt).FirstOrDefaultAsync(cancellationToken);
    }
}

/// <summary>
/// Vehicle GPS for fleet managers. Positions come from the driver's phone (<see cref="DriverTrackingEndpoints"/>).
/// <list type="bullet">
/// <item><c>GET /api/fleet/gps-status</c> (fleet View): the source, the settings and how many vehicles report.</item>
/// <item><c>GET /api/fleet/monitor</c> (fleet Edit): every vehicle's latest position, health and motion.</item>
/// <item><c>GET /api/vehicles/{id}/location</c> (fleet Edit): one vehicle's.</item>
/// <item><c>GET /api/vehicles/{id}/location-history?from&amp;to</c> (fleet Edit): the raw positions, at most 48 hours.</item>
/// <item><c>GET /api/vehicles/{id}/trips?date</c> (fleet Edit): the company day's trips, stops, gaps and route.</item>
/// <item><c>PUT /api/vehicles/{id}/tracking</c> (fleet Edit): turn tracking on or off for a vehicle.</item>
/// </list>
/// Positions are personal data: only fleet Edit (fleet managers; Executives by default) reads them. fleet View
/// alone, which every driver has, never sees a position.
/// </summary>
internal static class VehicleLocationEndpoints
{
    internal sealed record GpsStatusResponse(
        string Provider, bool Configured, int PolicyVersion, int RetentionDays, int TrackedVehicles, int ReportingVehicles, int SampleSeconds, int StopMinutes,
        int StaleMinutes, string Note);

    internal sealed record MonitorResponse(IReadOnlyList<FleetVehicleStatusView> Items, int TotalCount, DateTime ServerTime, int LiveMinutes, int StaleMinutes);

    internal sealed record HistoryResponse(int VehicleId, string Plate, DateTime From, DateTime To, bool Truncated, IReadOnlyList<GpsPositionView> Points);

    internal sealed record TrackingSwitchRequest(bool? Enabled, string? RowVersion);

    private const int MaxHistoryPoints = 5000;

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/fleet/gps-status", StatusAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("GetFleetGpsStatus")
            .WithSummary("Where vehicle positions come from (the driver's phone), the settings, and how many vehicles report");

        api.MapGet("/fleet/monitor", MonitorAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Edit)
            .WithName("GetFleetMonitor")
            .WithSummary("Every vehicle's latest known position with its age, tracking health and motion (fleet Edit). search, state, driverEmployeeId");

        api.MapGet("/vehicles/{vehicleId:int}/location", LatestAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Edit)
            .WithName("GetVehicleLocation")
            .WithSummary("The vehicle's latest known position, tracking health and motion (fleet Edit)");

        api.MapGet("/vehicles/{vehicleId:int}/location-history", HistoryAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Edit)
            .WithName("GetVehicleLocationHistory")
            .WithSummary("The vehicle's stored positions between from and to (UTC moments or dates; at most 48 hours, 5000 positions), fleet Edit");

        api.MapGet("/vehicles/{vehicleId:int}/trips", DayAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Edit)
            .WithName("GetVehicleDay")
            .WithSummary("The vehicle's company day (date=yyyy-MM-dd): trips, stops, data gaps, route and totals, with the driver of each trip (fleet Edit)");

        api.MapPut("/vehicles/{vehicleId:int}/tracking", SetTrackingAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Edit)
            .WithName("SetVehicleTracking")
            .WithSummary("Turn GPS tracking of the vehicle on or off (fleet Edit). rowVersion");
    }

    internal static async Task<Results<Ok<GpsStatusResponse>, ProblemHttpResult>> StatusAsync(
        HttpContext httpContext, FleetDbContext db, VehicleTrackingAlerts alerts, CompanyClock clock, IOptions<FleetOptions> settings, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var gps = settings.Value.Gps;
        var expected = await alerts.ExpectedAsync(cancellationToken);
        var liveSince = clock.UtcNow.AddMinutes(-gps.StaleMinutes);
        var reporting = await db.VehicleTrackingStates.AsNoTracking().CountAsync(state => state.LastPointAt >= liveSince, cancellationToken);
        return TypedResults.Ok(new GpsStatusResponse(
            gps.Provider, true, gps.PolicyVersion, gps.RetentionDays, expected.Count, reporting, gps.SampleSeconds, gps.StopMinutes, gps.StaleMinutes,
            "Positions come from the app on the assigned driver's phone, after the driver accepts the tracking notice. No external tracker is integrated."));
    }

    internal static async Task<Results<Ok<MonitorResponse>, ValidationProblem, ProblemHttpResult>> MonitorAsync(
        HttpContext httpContext, FleetDbContext db, FleetMonitor monitor, CompanyClock clock, IOptions<FleetOptions> settings, CancellationToken cancellationToken,
        string? search = null, string? state = null, int? driverEmployeeId = null, bool includeRetired = false)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var query = db.Vehicles.AsNoTracking().Include(vehicle => vehicle.Equipment).AsQueryable();
        if (!includeRetired)
        {
            query = query.Where(vehicle => vehicle.Equipment.Status != EquipmentStatus.Retired);
        }

        if (driverEmployeeId is { } driverId)
        {
            query = query.Where(vehicle => db.VehicleDriverAssignments.Any(assignment => assignment.VehicleId == vehicle.Id && assignment.EndDate == null && assignment.DriverEmployeeId == driverId));
        }

        // A fleet is tens of vehicles, not thousands: one answer, capped.
        var vehicles = await query.OrderBy(vehicle => vehicle.Plate).Take(500).ToListAsync(cancellationToken);
        IEnumerable<FleetVehicleStatusView> items = await monitor.StatusesAsync(vehicles, cancellationToken);
        if (Input.Clean(search) is { } text)
        {
            items = items.Where(item => Contains(item.Plate, text) || Contains(item.AssetNo, text) || Contains(item.NameAr, text) || Contains(item.NameEn, text)
                                        || Contains(item.Driver?.NameAr, text) || Contains(item.Driver?.NameEn, text) || Contains(item.Driver?.EmployeeNo, text));
        }

        if (Input.Clean(state) is { } wanted)
        {
            items = items.Where(item => string.Equals(item.Health.State, wanted, StringComparison.OrdinalIgnoreCase));
        }

        var list = items.ToList();
        var gps = settings.Value.Gps;
        return TypedResults.Ok(new MonitorResponse(list, list.Count, clock.UtcNow, gps.LiveMinutes, gps.StaleMinutes));

        static bool Contains(string? value, string text) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<Results<Ok<FleetVehicleStatusView>, ProblemHttpResult>> LatestAsync(
        int vehicleId, HttpContext httpContext, FleetDbContext db, FleetMonitor monitor, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var vehicle = await db.Vehicles.AsNoTracking().Include(row => row.Equipment).SingleOrDefaultAsync(row => row.Id == vehicleId, cancellationToken);
        return vehicle is null ? FleetProblems.VehicleNotFound() : TypedResults.Ok((await monitor.StatusesAsync([vehicle], cancellationToken))[0]);
    }

    internal static async Task<Results<Ok<HistoryResponse>, ValidationProblem, ProblemHttpResult>> HistoryAsync(
        int vehicleId, HttpContext httpContext, FleetDbContext db, CompanyClock clock, CancellationToken cancellationToken, string? from = null, string? to = null)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var end = Moment(to, clock, errors, "to", endOfDay: true) ?? clock.UtcNow;
        var start = Moment(from, clock, errors, "from", endOfDay: false) ?? end.AddHours(-24);
        if (!errors.HasErrors && end <= start)
        {
            errors.Add("to", "to must be after from.");
        }
        else if (!errors.HasErrors && end - start > TimeSpan.FromHours(48))
        {
            errors.Add("to", "Ask for at most 48 hours of positions at a time.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(row => row.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        var points = await db.VehicleGpsPoints.AsNoTracking()
            .Where(point => point.VehicleId == vehicleId && point.RecordedAt >= start && point.RecordedAt < end)
            .OrderBy(point => point.RecordedAt).ThenBy(point => point.Id)
            .Take(MaxHistoryPoints + 1)
            .Select(point => new GpsPositionView(point.RecordedAt, point.ReceivedAt, point.Latitude, point.Longitude, point.AccuracyMeters, point.SpeedKmh))
            .ToListAsync(cancellationToken);
        var truncated = points.Count > MaxHistoryPoints;
        return TypedResults.Ok(new HistoryResponse(vehicleId, vehicle.Plate, start, end, truncated, truncated ? points[..MaxHistoryPoints] : points));
    }

    internal static async Task<Results<Ok<GpsDayTimelineView>, ValidationProblem, ProblemHttpResult>> DayAsync(
        int vehicleId, HttpContext httpContext, FleetDbContext db, FleetMonitor monitor, CompanyClock clock, CancellationToken cancellationToken, string? date = null)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var day = Input.OptionalDate(date, errors, "date", "The date") ?? clock.Today;
        if (day > clock.Today)
        {
            errors.Add("date", "The date can't be in the future.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(row => row.Id == vehicleId, cancellationToken);
        return vehicle is null ? FleetProblems.VehicleNotFound() : TypedResults.Ok(await monitor.DayAsync(vehicle, day, cancellationToken));
    }

    internal static async Task<Results<Ok<FleetVehicleStatusView>, ValidationProblem, ProblemHttpResult>> SetTrackingAsync(
        int vehicleId, TrackingSwitchRequest request, HttpContext httpContext, FleetDbContext db, FleetMonitor monitor, VehicleTrackingAlerts alerts, CompanyClock clock,
        FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.Enabled is null)
        {
            errors.Add("enabled", "enabled is required.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var vehicle = await db.Vehicles.Include(row => row.Equipment).SingleOrDefaultAsync(row => row.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        var now = clock.UtcNow;
        vehicle.TrackingEnabled = request.Enabled!.Value;
        vehicle.UpdatedAt = now;
        vehicle.UpdatedBy = caller.UserId;
        db.Entry(vehicle).Property(row => row.RowVersion).OriginalValue = rowVersion!;
        if (!vehicle.TrackingEnabled)
        {
            // No longer expected: an open silence closes without a notification.
            var state = await db.VehicleTrackingStates.SingleOrDefaultAsync(row => row.VehicleId == vehicleId, cancellationToken);
            if (state is not null)
            {
                state.SilentSince = null;
            }

            await alerts.CloseAsync(vehicleId, VehicleTrackingAlertStatus.Ended, now, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.VehicleTrackingSwitched(caller.UserId, vehicleId, vehicle.TrackingEnabled ? "on" : "off");
        return TypedResults.Ok((await monitor.StatusesAsync([vehicle], cancellationToken))[0]);
    }

    /// <summary>An ISO moment (UTC when no offset is given) or a company date (its start, or the next day's start for <paramref name="endOfDay"/>).</summary>
    private static DateTime? Moment(string? value, CompanyClock clock, ValidationErrors errors, string field, bool endOfDay)
    {
        if (Input.Clean(value) is not { } text)
        {
            return null;
        }

        if (text.Length == 10 && DateOnly.TryParseExact(text, "yyyy-MM-dd", out var date))
        {
            return clock.StartOfDayUtc(endOfDay ? date.AddDays(1) : date);
        }

        if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var moment))
        {
            return moment.UtcDateTime;
        }

        errors.Add(field, $"{field} must be a date (yyyy-MM-dd) or an ISO 8601 moment.");
        return null;
    }
}
