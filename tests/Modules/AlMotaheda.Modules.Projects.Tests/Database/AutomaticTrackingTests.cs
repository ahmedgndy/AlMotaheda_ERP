using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.SiteLocation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database;

/// <summary>
/// Automatic tracking on the engineer's company phone (owner's brief of 2026-10-17, evening): no daily start button;
/// after the engineer accepts the policy once, the phone uploads its queue of positions and heartbeats. Each position
/// keeps the time it was taken and is stored once; only live positions open or close alerts; a low battery alerts once.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class AutomaticTrackingTests(SqlServerFixture database)
{
    // The project site of ProjectsApi.ProjectBody.
    private const decimal SiteLatitude = 30.1395m;
    private const decimal SiteLongitude = 31.7085m;
    private const string Phone = "phone-1";

    /// <summary>The defaults (all-day tracking), with the monitor waiting an hour so it never runs on its own.</summary>
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["SiteLocation:CheckEverySeconds"] = "3600",
    };

    private sealed record Team(Session Executive, int ManagerId, Session Manager, int EngineerId, Session Engineer, int ProjectId);

    private static async Task<(ProjectsApi Api, Team Team)> StartAsync(SqlServerFixture database)
    {
        var api = await ProjectsApi.StartAsync(database, Settings);
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineerId, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var project = await api.CreateProjectAsync(executive, "TRK-001", managerId, siteEngineers: [engineerId]);
        return (api, new Team(executive, managerId, manager, engineerId, engineer, project.GetProperty("id").GetInt32()));
    }

    private static Task<HttpResponseMessage> AcceptAsync(ProjectsApi api, Session session, int policyVersion = 1, string deviceId = Phone) =>
        api.SendAsync(HttpMethod.Put, "/api/site-location/me/policy", session, new { deviceId, platform = "Android", appVersion = "0.2.0", policyVersion });

    private static object Point(Guid id, DateTime recordedAt, decimal latitude, decimal longitude, int accuracyMeters = 10) =>
        new { id = id.ToString(), recordedAt, latitude, longitude, accuracyMeters };

    private static Task<HttpResponseMessage> BatchAsync(ProjectsApi api, Session session, object[] points, object[]? heartbeats = null, string deviceId = Phone) =>
        api.SendAsync(HttpMethod.Post, "/api/site-location/me/batch", session, new { deviceId, appVersion = "0.2.0", points, heartbeats = heartbeats ?? [] });

    private static object Heartbeat(DateTime recordedAt, int battery, bool charging) =>
        new { recordedAt, batteryPercent = battery, charging, trackingState = "Running", permission = "Always", gpsEnabled = true, pendingPoints = 0 };

    private static Task<List<LocationAlert>> AlertsAsync(ProjectsApi api, int projectId) =>
        api.QueryAsync(db => db.LocationAlerts.AsNoTracking().Where(alert => alert.ProjectId == projectId).OrderBy(alert => alert.Id).ToListAsync());

    private static async Task<JsonElement> EngineerOnMapAsync(ProjectsApi api, Team team)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{team.ProjectId}/site-location", team.Manager);
        await EnsureStatus(response, HttpStatusCode.OK);
        return Assert.Single((await Json(response)).GetProperty("items").EnumerateArray());
    }

    private static async Task WaitForNotificationAsync(ProjectsApi api, Session session, string type)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var response = await api.SendAsync(HttpMethod.Get, "/api/notifications?pageSize=50", session);
            await EnsureStatus(response, HttpStatusCode.OK);
            if ((await Json(response)).GetProperty("items").EnumerateArray().Any(item => item.GetProperty("type").GetString() == type))
            {
                return;
            }

            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"No {type} notification within 30 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    [DatabaseFact]
    public async Task Tracking_starts_by_itself_once_the_policy_is_accepted_on_the_phone()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;

        var before = await Json(await api.SendAsync(HttpMethod.Get, $"/api/site-location/me?deviceId={Phone}", team.Engineer));
        Assert.True(before.GetProperty("subject").GetBoolean());
        Assert.False(before.GetProperty("accepted").GetBoolean());
        Assert.True(before.GetProperty("settings").GetProperty("trackAllDay").GetBoolean());
        Assert.Equal("ProjectManagerAndExecutives", before.GetProperty("viewers").GetString());
        Assert.Equal(team.ProjectId, Assert.Single(before.GetProperty("projects").EnumerateArray()).GetProperty("projectId").GetInt32());

        // Nothing is kept before the policy is accepted on this phone: tracking is never silent.
        var early = await BatchAsync(api, team.Engineer, [Point(Guid.NewGuid(), DateTime.UtcNow, SiteLatitude, SiteLongitude)]);
        await EnsureStatus(early, HttpStatusCode.Conflict);
        Assert.Equal("tracking_policy_not_accepted", await CodeOf(early));

        await EnsureStatus(await AcceptAsync(api, team.Engineer, policyVersion: 0), HttpStatusCode.BadRequest);
        var accepted = await Json(await AcceptAsync(api, team.Engineer));
        Assert.True(accepted.GetProperty("accepted").GetBoolean());

        // No start button: the project's sharing is on, and the manager sees the phone.
        var onMap = await EngineerOnMapAsync(api, team);
        Assert.True(onMap.GetProperty("isOn").GetBoolean());
        Assert.Equal("Android", onMap.GetProperty("device").GetProperty("platform").GetString());

        // Another phone of the same engineer must accept too.
        var other = await BatchAsync(api, team.Engineer, [Point(Guid.NewGuid(), DateTime.UtcNow, SiteLatitude, SiteLongitude)], deviceId: "phone-2");
        await EnsureStatus(other, HttpStatusCode.Conflict);

        // Someone who is not a site engineer anywhere is not a subject.
        var manager = await Json(await api.SendAsync(HttpMethod.Get, "/api/site-location/me", team.Manager));
        Assert.False(manager.GetProperty("subject").GetBoolean());
    }

    [DatabaseFact]
    public async Task A_queue_sent_twice_is_stored_once_with_the_times_the_phone_took_each_position()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;
        await EnsureStatus(await AcceptAsync(api, team.Engineer), HttpStatusCode.OK);

        var now = DateTime.UtcNow;
        var taken = new[] { now.AddHours(-3), now.AddHours(-2), now.AddMinutes(-61) };
        var ids = taken.Select(_ => Guid.NewGuid()).ToArray();
        object[] queue =
        [
            Point(ids[0], taken[0], SiteLatitude, SiteLongitude),
            Point(ids[1], taken[1], SiteLatitude + 0.0005m, SiteLongitude),
            Point(ids[2], taken[2], SiteLatitude + 0.001m, SiteLongitude),
            Point(Guid.NewGuid(), now.AddDays(-8), SiteLatitude, SiteLongitude), // older than MaxLateDays
            Point(Guid.NewGuid(), now.AddHours(1), SiteLatitude, SiteLongitude), // a wrong clock
            new { id = "not-a-guid", recordedAt = now, latitude = SiteLatitude, longitude = SiteLongitude },
        ];

        var first = await Json(await BatchAsync(api, team.Engineer, queue));
        Assert.Equal(3, first.GetProperty("accepted").GetInt32());
        Assert.Equal(0, first.GetProperty("duplicates").GetInt32());
        Assert.Equal(["future", "id", "tooOld"], first.GetProperty("rejected").EnumerateArray().Select(item => item.GetProperty("reason").GetString()).Order(StringComparer.Ordinal));

        // The answer was lost and the phone sends the same queue again: nothing is stored twice.
        var again = await Json(await BatchAsync(api, team.Engineer, queue));
        Assert.Equal(0, again.GetProperty("accepted").GetInt32());
        Assert.Equal(3, again.GetProperty("duplicates").GetInt32());
        Assert.Equal(3, await api.QueryAsync(db => db.LocationPings.CountAsync(ping => ping.UserId == team.EngineerId)));

        // The history keeps the phone's times, not the upload time.
        var stored = await api.QueryAsync(db => db.LocationPings.AsNoTracking().Where(ping => ping.UserId == team.EngineerId).OrderBy(ping => ping.RecordedAt).ToListAsync());
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(taken[i], stored[i].RecordedAt, TimeSpan.FromMilliseconds(5));
            Assert.True(stored[i].ReceivedAt - stored[i].RecordedAt > TimeSpan.FromMinutes(60));
        }

        // The map's "last seen" is the newest position's own time.
        var onMap = await EngineerOnMapAsync(api, team);
        Assert.Equal(taken[2], onMap.GetProperty("lastPingAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(5));
    }

    [DatabaseFact]
    public async Task Positions_sent_late_never_raise_alerts_but_live_ones_do_with_their_own_time()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;
        await EnsureStatus(await AcceptAsync(api, team.Engineer), HttpStatusCode.OK);
        var now = DateTime.UtcNow;

        // Three positions far outside the site, taken two hours ago while the phone was offline: history only.
        var outside = SiteLatitude + 0.06m;
        await EnsureStatus(await BatchAsync(api, team.Engineer,
        [
            Point(Guid.NewGuid(), now.AddMinutes(-125), outside, SiteLongitude),
            Point(Guid.NewGuid(), now.AddMinutes(-124), outside, SiteLongitude),
            Point(Guid.NewGuid(), now.AddMinutes(-123), outside, SiteLongitude),
        ]), HttpStatusCode.OK);
        Assert.Empty(await AlertsAsync(api, team.ProjectId));

        // Two live positions outside: the alert opens at the second one's time.
        var second = now.AddSeconds(-30);
        await EnsureStatus(await BatchAsync(api, team.Engineer,
        [
            Point(Guid.NewGuid(), now.AddMinutes(-1), outside, SiteLongitude),
            Point(Guid.NewGuid(), second, outside, SiteLongitude),
        ]), HttpStatusCode.OK);
        var alerts = await AlertsAsync(api, team.ProjectId);
        Assert.Equal(new[] { team.ManagerId, team.Executive.UserId }.Order(), alerts.Select(alert => alert.RecipientUserId).Order());
        Assert.All(alerts, alert =>
        {
            Assert.IsType<OutsideSiteAlert>(alert);
            Assert.Equal(second, alert.StartedAt, TimeSpan.FromMilliseconds(5));
        });
        await WaitForNotificationAsync(api, team.Manager, "OutsideSite");

        // Back inside: closed at that position's time.
        var back = DateTime.UtcNow;
        await EnsureStatus(await BatchAsync(api, team.Engineer, [Point(Guid.NewGuid(), back, SiteLatitude, SiteLongitude)]), HttpStatusCode.OK);
        Assert.All(await AlertsAsync(api, team.ProjectId), alert =>
        {
            Assert.Equal(LocationAlertStatus.Recovered, alert.Status);
            Assert.Equal(back, alert.EndedAt!.Value, TimeSpan.FromMilliseconds(5));
        });
    }

    [DatabaseFact]
    public async Task An_engineer_at_another_of_their_project_sites_is_not_outside()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;

        // A second project about 6.7 km north, with the same site engineer.
        var response = await api.SendAsync(HttpMethod.Post, "/api/projects", team.Executive, ProjectsApi.ProjectBody("TRK-002", team.ManagerId, new()
        {
            ["siteLatitude"] = SiteLatitude + 0.06m,
            ["siteEngineerUserIds"] = new[] { team.EngineerId },
        }));
        await EnsureStatus(response, HttpStatusCode.Created);
        var otherProject = (await Json(response)).GetProperty("id").GetInt32();
        await EnsureStatus(await AcceptAsync(api, team.Engineer), HttpStatusCode.OK);

        var now = DateTime.UtcNow;
        var result = await Json(await BatchAsync(api, team.Engineer,
        [
            Point(Guid.NewGuid(), now.AddMinutes(-2), SiteLatitude + 0.06m, SiteLongitude),
            Point(Guid.NewGuid(), now.AddMinutes(-1), SiteLatitude + 0.06m, SiteLongitude),
        ]));

        Assert.Equal(2, result.GetProperty("projects").GetInt32());
        Assert.Empty(await AlertsAsync(api, team.ProjectId));
        Assert.Empty(await AlertsAsync(api, otherProject));
        Assert.Equal(2, await api.QueryAsync(db => db.LocationPings.CountAsync(ping => ping.ProjectId == otherProject && ping.InsideSite == true)));
    }

    [DatabaseFact]
    public async Task A_low_battery_alerts_once_while_the_phone_reports_and_charging_ends_it()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;
        await EnsureStatus(await AcceptAsync(api, team.Engineer), HttpStatusCode.OK);

        // A low battery reported late (the phone was offline) says nothing about now.
        await EnsureStatus(await BatchAsync(api, team.Engineer, [], [Heartbeat(DateTime.UtcNow.AddHours(-2), 8, false)]), HttpStatusCode.OK);
        Assert.Empty(await AlertsAsync(api, team.ProjectId));

        await EnsureStatus(await BatchAsync(api, team.Engineer, [], [Heartbeat(DateTime.UtcNow.AddSeconds(-5), 12, false)]), HttpStatusCode.OK);
        await EnsureStatus(await BatchAsync(api, team.Engineer, [], [Heartbeat(DateTime.UtcNow, 11, false)]), HttpStatusCode.OK);
        var alerts = await AlertsAsync(api, team.ProjectId);
        Assert.Equal(2, alerts.Count); // the manager and the Executive, once
        Assert.All(alerts, alert => Assert.IsType<LowBatteryAlert>(alert));
        await WaitForNotificationAsync(api, team.Manager, "LowBattery");

        var device = (await EngineerOnMapAsync(api, team)).GetProperty("device");
        Assert.Equal(11, device.GetProperty("batteryPercent").GetInt32());
        Assert.Equal("Running", device.GetProperty("trackingState").GetString());
        Assert.Equal(JsonValueKind.String, device.GetProperty("lowBatterySince").ValueKind);

        await EnsureStatus(await BatchAsync(api, team.Engineer, [], [Heartbeat(DateTime.UtcNow, 13, true)]), HttpStatusCode.OK);
        Assert.All(await AlertsAsync(api, team.ProjectId), alert => Assert.Equal(LocationAlertStatus.Recovered, alert.Status));
        Assert.Equal(JsonValueKind.Null, (await EngineerOnMapAsync(api, team)).GetProperty("device").GetProperty("lowBatterySince").ValueKind);

        // The alert history shows one episode.
        var history = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{team.ProjectId}/site-location/alerts", team.Manager));
        var episode = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal("LowBattery", episode.GetProperty("kind").GetString());
        await EnsureStatus(await api.SendAsync(HttpMethod.Get, $"/api/projects/{team.ProjectId}/site-location/alerts", team.Engineer), HttpStatusCode.Forbidden);
    }

    [DatabaseFact]
    public async Task Silence_alerts_at_any_hour_and_only_a_live_queued_position_ends_the_outage()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;
        await EnsureStatus(await AcceptAsync(api, team.Engineer), HttpStatusCode.OK);

        // Silent for 20 minutes: with all-day tracking that is an outage whatever the hour.
        var now = await api.QueryAsync(async db =>
        {
            var sharing = await db.LocationSharings.SingleAsync(row => row.UserId == team.EngineerId);
            sharing.TurnedOnAt = DateTime.UtcNow.AddMinutes(-40);
            sharing.LastPingAt = DateTime.UtcNow.AddMinutes(-20);
            await db.SaveChangesAsync();
            return DateTime.UtcNow;
        });
        await using (var scope = api.Access.App.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SiteLocationAlerts>().CheckAsync(now, CancellationToken.None);
        }

        Assert.All(await AlertsAsync(api, team.ProjectId), alert => Assert.IsType<LocationUnavailableAlert>(alert));
        Assert.Equal("Unavailable", (await EngineerOnMapAsync(api, team)).GetProperty("state").GetString());

        // The phone comes back and uploads its queue: an old position keeps the outage open, a live one ends it.
        await EnsureStatus(await BatchAsync(api, team.Engineer, [Point(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-18), SiteLatitude, SiteLongitude)]), HttpStatusCode.OK);
        Assert.All(await AlertsAsync(api, team.ProjectId), alert => Assert.Equal(LocationAlertStatus.Open, alert.Status));
        // A queued position taken before the outage was detected, arriving live: the outage ends when it arrives, never
        // before it started (QA OFF-003).
        await EnsureStatus(await BatchAsync(api, team.Engineer, [Point(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(-2), SiteLatitude, SiteLongitude)]), HttpStatusCode.OK);
        Assert.All(await AlertsAsync(api, team.ProjectId), alert =>
        {
            Assert.Equal(LocationAlertStatus.Recovered, alert.Status);
            Assert.True(alert.EndedAt >= alert.StartedAt, "an outage can't end before it started");
        });
        Assert.Equal("OnSite", (await EngineerOnMapAsync(api, team)).GetProperty("state").GetString());
        await WaitForNotificationAsync(api, team.Manager, "LocationAvailable");
    }
}
