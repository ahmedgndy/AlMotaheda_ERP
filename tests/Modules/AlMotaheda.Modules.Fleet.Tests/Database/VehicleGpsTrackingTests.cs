using System.Globalization;
using System.Net;
using System.Text.Json;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Gps;
using AlMotaheda.Modules.Projects.Tests.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Fleet.Tests.Database;

/// <summary>
/// Vehicle GPS from the driver's phone on SQL Server, through the real endpoints: the notice accepted once, a queue
/// of positions taken offline stored once with their capture time (G, H, I), positions that keep the vehicle and
/// driver of their capture time across a hand-over (J), refused positions (K), who may read positions (P), the
/// stale-vehicle alert opened once and closed by the next live position (N), and a trip across midnight (F).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class VehicleGpsTrackingTests(SqlServerFixture database)
{
    private const double Lat0 = 30.0444, Lng0 = 31.2357, Step = 0.005;

    [DatabaseFact]
    public async Task An_offline_queue_is_stored_once_with_its_capture_times_and_rebuilds_the_route()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await CastAsync(api);

        // The phone asks first: the driver is tracked for the pickup, but nothing is kept before the notice is accepted.
        var config = await Ok(api, HttpMethod.Get, $"/api/fleet/tracking/me?deviceId={cast.DeviceId}", cast.Driver);
        Assert.Equal((true, false, cast.PickupId), (config.GetProperty("subject").GetBoolean(), config.GetProperty("accepted").GetBoolean(),
            config.GetProperty("vehicle").GetProperty("vehicleId").GetInt32()));
        var refused = await api.SendAsync(HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.Driver, Batch(cast.DeviceId, Drive(Now(api).AddHours(-3), 3, Lat0)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("vehicle_tracking_notice_not_accepted", await CodeOf(refused));
        await AcceptAsync(api, cast.Driver, cast.DeviceId);

        // G/H: three hours ago the phone had no network: 30 positions a minute apart waited in its queue.
        var takenAt = Now(api).AddHours(-3);
        var queue = Drive(takenAt, 10, Lat0).Concat(Park(takenAt.AddMinutes(11), 8, Lat0 + 10 * Step)).Concat(Drive(takenAt.AddMinutes(20), 10, Lat0 + 10 * Step).Skip(1)).ToList();
        var first = await Ok(api, HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.Driver, Batch(cast.DeviceId, queue));
        Assert.Equal((queue.Count, 0, 0), (first.GetProperty("accepted").GetInt32(), first.GetProperty("duplicates").GetInt32(), first.GetProperty("rejected").GetArrayLength()));

        // I: the answer was lost (timeout) and the phone sends the same queue again: nothing is stored twice.
        var again = await Ok(api, HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.Driver, Batch(cast.DeviceId, queue));
        Assert.Equal((0, queue.Count), (again.GetProperty("accepted").GetInt32(), again.GetProperty("duplicates").GetInt32()));

        var stored = await api.FleetQueryAsync(db => db.VehicleGpsPoints.OrderBy(point => point.RecordedAt).ToListAsync());
        Assert.Equal(queue.Count, stored.Count);
        Assert.All(stored, point => Assert.Equal((cast.PickupId, cast.DriverEmployee), (point.VehicleId, point.DriverEmployeeId)));
        Assert.Equal(queue.Select(point => point.At), stored.Select(point => point.RecordedAt));
        Assert.All(stored, point => Assert.True(point.ReceivedAt - point.RecordedAt > TimeSpan.FromHours(2), "the capture time is never the upload time"));

        // The route of the day, rebuilt from the stored positions: two trips around one stop, the same on every read.
        var date = Iso(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(takenAt, Clock(api).Zone)));
        var day = await Ok(api, HttpMethod.Get, $"/api/vehicles/{cast.PickupId}/trips?date={date}", cast.Executive);
        Assert.Equal(("Recorded", 2, 1), (day.GetProperty("dataStatus").GetString(), day.GetProperty("trips").GetArrayLength(), day.GetProperty("stops").GetArrayLength()));
        Assert.InRange(day.GetProperty("summary").GetProperty("distanceKm").GetDouble(), 10.8, 11.4);
        Assert.Equal("Hany", day.GetProperty("trips")[0].GetProperty("driver").GetProperty("nameEn").GetString());
        Assert.Equal(day.ToString(), (await Ok(api, HttpMethod.Get, $"/api/vehicles/{cast.PickupId}/trips?date={date}", cast.Executive)).ToString());

        // N: the vehicle is shown where it last was, three hours ago, and not as moving now.
        var monitor = await Ok(api, HttpMethod.Get, "/api/fleet/monitor", cast.Executive);
        var pickup = monitor.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("vehicleId").GetInt32() == cast.PickupId);
        Assert.Equal(queue[^1].At, pickup.GetProperty("lastPosition").GetProperty("recordedAt").GetDateTime());
        Assert.Equal("Unknown", pickup.GetProperty("motion").GetProperty("state").GetString());
        Assert.Contains(pickup.GetProperty("health").GetProperty("state").GetString(), new[] { "Offline", "NoFix" });
    }

    [DatabaseFact]
    public async Task Queued_positions_keep_the_vehicle_and_driver_of_their_capture_time_after_a_hand_over()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await CastAsync(api);
        await AcceptAsync(api, cast.Driver, cast.DeviceId);
        await AcceptAsync(api, cast.OtherDriver, "other-phone-1");
        var today = Clock(api).Today;

        // J: Hany drove the pickup until yesterday; from today he drives the truck, and Samir takes the pickup.
        var drivers = await Ok(api, HttpMethod.Get, $"/api/vehicles/{cast.PickupId}/drivers", cast.Executive);
        var open = drivers.GetProperty("items")[0];
        await Ok(api, HttpMethod.Post, $"/api/vehicles/{cast.PickupId}/drivers/{open.Id()}/end", cast.Executive,
            new { endDate = Iso(today.AddDays(-1)), rowVersion = open.GetProperty("rowVersion").GetString() });
        await Created(api, cast.Executive, $"/api/vehicles/{cast.TruckId}/drivers", new { driverEmployeeId = cast.DriverEmployee, startDate = Iso(today) });
        await Created(api, cast.Executive, $"/api/vehicles/{cast.PickupId}/drivers", new { driverEmployeeId = cast.OtherDriverEmployee, startDate = Iso(today) });

        // Hany's phone was offline since two days ago: its queue holds pickup positions (then) and truck positions
        // (now), all still tagged with the pickup assignment it knew. The day of each position decides.
        var twoDaysAgo = Clock(api).StartOfDayUtc(today.AddDays(-2)).AddHours(9);
        var queue = Drive(twoDaysAgo, 5, Lat0).Concat(Drive(Now(api).AddMinutes(-20), 5, Lat0 + 0.2))
            .Select(point => point with { AssignmentId = open.Id() }).ToList();
        var result = await Ok(api, HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.Driver, Batch(cast.DeviceId, queue));
        Assert.Equal(12, result.GetProperty("accepted").GetInt32());

        var stored = await api.FleetQueryAsync(db => db.VehicleGpsPoints.OrderBy(point => point.RecordedAt).ToListAsync());
        Assert.All(stored.Take(6), point => Assert.Equal((cast.PickupId, cast.DriverEmployee), (point.VehicleId, point.DriverEmployeeId)));
        Assert.All(stored.Skip(6), point => Assert.Equal((cast.TruckId, cast.DriverEmployee), (point.VehicleId, point.DriverEmployeeId)));

        // The pickup's day two days ago still shows Hany, although Samir drives it now.
        var day = await Ok(api, HttpMethod.Get, $"/api/vehicles/{cast.PickupId}/trips?date={Iso(today.AddDays(-2))}", cast.Executive);
        Assert.Equal("Hany", Assert.Single(day.GetProperty("trips").EnumerateArray()).GetProperty("driver").GetProperty("nameEn").GetString());

        // K/P: Samir can't file positions for days he had no vehicle, nor tag another driver's assignment.
        var truckAssignment = stored[^1].AssignmentId;
        var points = new[]
        {
            Point(Guid.NewGuid(), twoDaysAgo, Lat0, Lng0),
            Point(Guid.NewGuid(), Now(api).AddMinutes(-1), Lat0, Lng0, assignmentId: truckAssignment),
            Point(Guid.NewGuid(), Now(api).AddMinutes(-1), 95, Lng0),
            Point(Guid.NewGuid(), Now(api).AddDays(1), Lat0, Lng0),
        };
        var refused = await Ok(api, HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.OtherDriver, new { deviceId = "other-phone-1", points });
        Assert.Equal(["noAssignment", "assignment", "position", "future"], refused.GetProperty("rejected").EnumerateArray().Select(row => row.GetProperty("reason").GetString()!));
        Assert.Equal(12, await api.FleetQueryAsync(db => db.VehicleGpsPoints.CountAsync()));
    }

    [DatabaseFact]
    public async Task Only_fleet_managers_read_positions_and_tracking_switched_off_keeps_nothing()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await CastAsync(api);
        await AcceptAsync(api, cast.Driver, cast.DeviceId);
        await Ok(api, HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.Driver, Batch(cast.DeviceId, Drive(Now(api).AddMinutes(-10), 5, Lat0)));

        // P: the driver, the manager (fleet View, Create, See money) and the accountant read no position; the Executive does.
        foreach (var session in new[] { cast.Driver, cast.Manager, cast.Accountant })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{cast.PickupId}/trips", session)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/fleet/monitor", session)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/vehicles/{cast.PickupId}/location-history", session)).StatusCode);
        }

        var history = await Ok(api, HttpMethod.Get, $"/api/vehicles/{cast.PickupId}/location-history?from={Iso(Clock(api).Today.AddDays(-1))}", cast.Executive);
        Assert.Equal(6, history.GetProperty("points").GetArrayLength());

        // A driver can't hand the vehicle to themself.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/vehicles/{cast.TruckId}/drivers", cast.Driver, new { driverEmployeeId = cast.DriverEmployee })).StatusCode);

        // The Executive turns tracking off for the pickup: the phone is told it is no longer tracked, and positions are refused.
        var vehicle = await Ok(api, HttpMethod.Get, $"/api/vehicles/{cast.PickupId}", cast.Executive);
        var off = await Ok(api, HttpMethod.Put, $"/api/vehicles/{cast.PickupId}/tracking", cast.Executive, new { enabled = false, rowVersion = vehicle.GetProperty("rowVersion").GetString() });
        Assert.Equal(("NotTracked", false), (off.GetProperty("health").GetProperty("state").GetString(), off.GetProperty("trackingEnabled").GetBoolean()));
        Assert.False((await Ok(api, HttpMethod.Get, $"/api/fleet/tracking/me?deviceId={cast.DeviceId}", cast.Driver)).GetProperty("subject").GetBoolean());
        var later = await Ok(api, HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.Driver, Batch(cast.DeviceId, Drive(Now(api).AddMinutes(-2), 1, Lat0)));
        Assert.Equal((0, "trackingOff"), (later.GetProperty("accepted").GetInt32(), later.GetProperty("rejected")[0].GetProperty("reason").GetString()));
    }

    [DatabaseFact]
    public async Task A_silent_vehicle_alerts_the_executives_once_and_its_next_live_position_closes_the_alert()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await CastAsync(api);
        await AcceptAsync(api, cast.Driver, cast.DeviceId);

        // The pickup reported until an hour ago (its assignment and the notice are older than that).
        await api.FleetQueryAsync(db => db.VehicleDriverAssignments.ExecuteUpdateAsync(set => set.SetProperty(row => row.AssignedAt, Now(api).AddHours(-5))));
        await api.FleetQueryAsync(db => db.VehicleTrackingDevices.ExecuteUpdateAsync(set => set.SetProperty(row => row.PolicyAcceptedAt, Now(api).AddHours(-5))));
        var lastHour = Now(api).AddHours(-1);
        await Ok(api, HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.Driver, Batch(cast.DeviceId, Drive(lastHour.AddMinutes(-5), 5, Lat0)));

        // Two monitor rounds: one silence, one alert per Executive, not two.
        await api.WithServicesAsync(async services => { await services.GetRequiredService<VehicleTrackingAlerts>().CheckAsync(Now(api), CancellationToken.None); return 0; });
        await api.WithServicesAsync(async services => { await services.GetRequiredService<VehicleTrackingAlerts>().CheckAsync(Now(api), CancellationToken.None); return 0; });
        var alerts = await api.FleetQueryAsync(db => db.VehicleTrackingAlerts.ToListAsync());
        var alert = Assert.Single(alerts);
        Assert.Equal((cast.PickupId, cast.Executive.UserId, VehicleTrackingAlertStatus.Open, lastHour), (alert.VehicleId, alert.RecipientUserId, alert.Status, alert.LastPointAt));
        var state = await api.FleetQueryAsync(db => db.VehicleTrackingStates.SingleAsync());
        Assert.Equal(lastHour, state.SilentSince);

        // The phone comes back with its queue and a live position: the silence ends, once.
        await Ok(api, HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.Driver, Batch(cast.DeviceId, Drive(Now(api).AddMinutes(-1), 1, Lat0 + Step)));
        alert = await api.FleetQueryAsync(db => db.VehicleTrackingAlerts.SingleAsync());
        Assert.Equal(VehicleTrackingAlertStatus.Recovered, alert.Status);
        Assert.Null((await api.FleetQueryAsync(db => db.VehicleTrackingStates.SingleAsync())).SilentSince);
    }

    [DatabaseFact]
    public async Task A_trip_across_midnight_belongs_to_both_company_days_and_each_day_counts_its_own_part()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await CastAsync(api);
        await AcceptAsync(api, cast.Driver, cast.DeviceId);
        var today = Clock(api).Today;
        var midnight = Clock(api).StartOfDayUtc(today.AddDays(-1));

        // 23:50 to 00:10 company time, the night before last: one trip, ten minutes on each side of midnight.
        await Ok(api, HttpMethod.Post, "/api/fleet/tracking/me/batch", cast.Driver, Batch(cast.DeviceId, Drive(midnight.AddMinutes(-10), 20, Lat0)));

        var yesterday = await Ok(api, HttpMethod.Get, $"/api/vehicles/{cast.PickupId}/trips?date={Iso(today.AddDays(-2))}", cast.Executive);
        var todayView = await Ok(api, HttpMethod.Get, $"/api/vehicles/{cast.PickupId}/trips?date={Iso(today.AddDays(-1))}", cast.Executive);
        var before = Assert.Single(yesterday.GetProperty("trips").EnumerateArray());
        var after = Assert.Single(todayView.GetProperty("trips").EnumerateArray());
        Assert.Equal((false, true), (before.GetProperty("startsBeforeDay").GetBoolean(), before.GetProperty("endsAfterDay").GetBoolean()));
        Assert.Equal((true, false), (after.GetProperty("startsBeforeDay").GetBoolean(), after.GetProperty("endsAfterDay").GetBoolean()));
        Assert.Equal(before.GetProperty("startedAt").GetDateTime(), after.GetProperty("startedAt").GetDateTime());
        // Each day counts the stretches that end inside it: 9 minutes before midnight, 11 from midnight on.
        Assert.InRange(yesterday.GetProperty("summary").GetProperty("distanceKm").GetDouble(), 4.9, 5.1);
        Assert.InRange(todayView.GetProperty("summary").GetProperty("distanceKm").GetDouble(), 6.0, 6.2);
        Assert.Equal("NoPositions", (await Ok(api, HttpMethod.Get, $"/api/vehicles/{cast.TruckId}/trips?date={Iso(today)}", cast.Executive)).GetProperty("dataStatus").GetString());
    }

    private sealed record Position(Guid Id, DateTime At, double Latitude, double Longitude, int? AssignmentId = null);

    private static IEnumerable<Position> Drive(DateTime from, int minutes, double latitude) =>
        Enumerable.Range(0, minutes + 1).Select(index => new Position(Guid.NewGuid(), from.AddMinutes(index), latitude + index * Step, Lng0));

    private static IEnumerable<Position> Park(DateTime from, int minutes, double latitude) =>
        Enumerable.Range(0, minutes + 1).Select(index => new Position(Guid.NewGuid(), from.AddMinutes(index), latitude + (index % 2 == 0 ? 0.0001 : -0.0001), Lng0));

    private static object Point(Guid id, DateTime at, double latitude, double longitude, int? assignmentId = null) => new
    {
        id = id.ToString(), recordedAt = at.ToString("O", CultureInfo.InvariantCulture), latitude, longitude, accuracyMeters = 8, speedMps = 9.2, heading = 12, assignmentId,
    };

    private static object Batch(string deviceId, IEnumerable<Position> points) => new
    {
        deviceId,
        appVersion = "0.3.0",
        points = points.Select(point => Point(point.Id, point.At, point.Latitude, point.Longitude, point.AssignmentId)).ToList(),
        heartbeats = new[] { new { recordedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), batteryPercent = 80, charging = false, trackingState = "Running", permission = "Always", gpsEnabled = true, pendingPoints = 0 } },
    };

    private static async Task AcceptAsync(ProjectsApi api, Session driver, string deviceId)
    {
        var accepted = await Ok(api, HttpMethod.Put, "/api/fleet/tracking/me/policy", driver, new { deviceId, platform = "Android", appVersion = "0.3.0", policyVersion = 1 });
        Assert.True(accepted.GetProperty("accepted").GetBoolean());
    }

    /// <summary>The usual Fleet cast, two staff drivers (HR employees), a pickup driven by the first for a month and a truck with no driver.</summary>
    private static async Task<GpsCast> CastAsync(ProjectsApi api)
    {
        var cast = await api.CastAsync();
        var (driverUserId, driver) = await api.CreateSignedInUserAsync(cast.Executive, "staff");
        var (otherUserId, otherDriver) = await api.CreateSignedInUserAsync(cast.Executive, "staff");
        var department = (await Created(api, cast.Executive, "/api/hr/departments", new { nameAr = "الحركة", nameEn = "Transport" })).Id();
        var driverEmployee = (await Created(api, cast.Executive, "/api/hr/employees", Employee("Hany", "29005051234567", department, driverUserId))).Id();
        var otherEmployee = (await Created(api, cast.Executive, "/api/hr/employees", Employee("Samir", "29006061234567", department, otherUserId))).Id();
        var pickup = await Created(api, cast.Executive, "/api/vehicles", new
        {
            assetNo = "PK-301", nameAr = "بيك أب 301", nameEn = "Pickup 301", plate = "ب ص ع 3010", type = "Pickup", fuelType = "Diesel", odometerKm = 10_000m,
        });
        var truck = await Created(api, cast.Executive, "/api/vehicles", new
        {
            assetNo = "TR-302", nameAr = "نقل 302", nameEn = "Truck 302", plate = "ن ق ل 3020", type = "Truck", fuelType = "Diesel", odometerKm = 50_000m,
        });
        Assert.True(pickup.GetProperty("trackingEnabled").GetBoolean());
        await Created(api, cast.Executive, $"/api/vehicles/{pickup.Id()}/drivers", new { driverEmployeeId = driverEmployee, startDate = Iso(Clock(api).Today.AddDays(-30)) });
        return new GpsCast(cast.Executive, cast.Manager, cast.Accountant, driver, otherDriver, driverEmployee, otherEmployee, pickup.Id(), truck.Id(), "hany-phone-1");
    }

    private static object Employee(string name, string nationalId, int departmentId, int userId) => new
    {
        nameAr = $"{name} بالعربية", nameEn = name, nationalId, jobTitle = "Driver", departmentId, kind = "Staff", hireDate = "2024-06-01", userId,
    };

    private static async Task<JsonElement> Ok(ProjectsApi api, HttpMethod method, string path, Session session, object? body = null)
    {
        var response = await api.SendAsync(method, path, session, body);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    private static async Task<JsonElement> Created(ProjectsApi api, Session session, string path, object body)
    {
        var response = await api.SendAsync(HttpMethod.Post, path, session, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    private static CompanyClock Clock(ProjectsApi api) => api.Access.App.Services.GetRequiredService<CompanyClock>();

    /// <summary>Now, to the whole second (positions are stored to the millisecond).</summary>
    private static DateTime Now(ProjectsApi api)
    {
        var now = Clock(api).UtcNow;
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record GpsCast(
        Session Executive, Session Manager, Session Accountant, Session Driver, Session OtherDriver, int DriverEmployee, int OtherDriverEmployee, int PickupId, int TruckId,
        string DeviceId);
}
