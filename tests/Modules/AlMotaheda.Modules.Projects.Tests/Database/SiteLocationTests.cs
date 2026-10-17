using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.SiteLocation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database;

/// <summary>
/// Site engineers' location sharing, with the owner's rules: only the project's site engineers share, only after
/// turning it on; only the project manager and Executives see it; silence and leaving the site area alert them once
/// per outage, and the end of the outage once.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SiteLocationTests(SqlServerFixture database)
{
    // The project site of ProjectsApi.ProjectBody.
    private const decimal SiteLatitude = 30.1395m;
    private const decimal SiteLongitude = 31.7085m;

    /// <summary>Every day is a working day, all day, so the tests pass at any hour; the monitor waits an hour.</summary>
    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["SiteLocation:WorkDays:0"] = "Friday",
        ["SiteLocation:WorkDays:1"] = "Saturday",
        ["SiteLocation:WorkDays:2"] = "Sunday",
        ["SiteLocation:WorkDays:3"] = "Monday",
        ["SiteLocation:WorkDays:4"] = "Tuesday",
        ["SiteLocation:WorkDays:5"] = "Wednesday",
        ["SiteLocation:WorkDays:6"] = "Thursday",
        ["SiteLocation:WorkStart"] = "00:00",
        ["SiteLocation:WorkEnd"] = "23:59",
        ["SiteLocation:CheckEverySeconds"] = "3600",
    };

    private sealed record Team(Session Executive, int ManagerId, Session Manager, int EngineerId, Session Engineer, int ProjectId);

    private static async Task<(ProjectsApi Api, Team Team)> StartAsync(SqlServerFixture database)
    {
        var api = await ProjectsApi.StartAsync(database, Settings);
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineerId, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var project = await api.CreateProjectAsync(executive, "GPS-001", managerId, siteEngineers: [engineerId]);
        return (api, new Team(executive, managerId, manager, engineerId, engineer, project.GetProperty("id").GetInt32()));
    }

    private static string Base(Team team) => $"/api/projects/{team.ProjectId}/site-location";

    private static Task<HttpResponseMessage> TurnAsync(ProjectsApi api, Team team, Session session, bool isOn) =>
        api.SendAsync(HttpMethod.Put, $"{Base(team)}/me", session, new { isOn });

    private static Task<HttpResponseMessage> PositionAsync(ProjectsApi api, Team team, decimal latitude, decimal longitude, int? accuracyMeters = 10) =>
        api.SendAsync(HttpMethod.Post, $"{Base(team)}/me/positions", team.Engineer, new { latitude, longitude, accuracyMeters });

    private static async Task<JsonElement> EngineerOnMapAsync(ProjectsApi api, Team team)
    {
        var response = await api.SendAsync(HttpMethod.Get, Base(team), team.Manager);
        await EnsureStatus(response, HttpStatusCode.OK);
        return Assert.Single((await Json(response)).GetProperty("items").EnumerateArray());
    }

    private static async Task<T> WithAlertsAsync<T>(ProjectsApi api, Func<SiteLocationAlerts, CompanyClock, Task<T>> action)
    {
        await using var scope = api.Access.App.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<SiteLocationAlerts>(), scope.ServiceProvider.GetRequiredService<CompanyClock>());
    }

    /// <summary>Yesterday at noon, company time: a moment inside working hours, before any position sent now.</summary>
    private static DateTime YesterdayNoon(CompanyClock clock) =>
        TimeZoneInfo.ConvertTimeToUtc(clock.Today.AddDays(-1).ToDateTime(new TimeOnly(12, 0)), clock.Zone);

    /// <summary>Makes the engineer's phone look silent since 20 minutes before <paramref name="noon"/>, then runs the monitor then.</summary>
    private static async Task GoSilentAsync(ProjectsApi api, Team team, DateTime noon)
    {
        await api.QueryAsync(db => db.LocationSharings
            .Where(sharing => sharing.ProjectId == team.ProjectId && sharing.UserId == team.EngineerId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(sharing => sharing.TurnedOnAt, noon.AddMinutes(-30))
                .SetProperty(sharing => sharing.LastPingAt, noon.AddMinutes(-20))));
        await WithAlertsAsync(api, async (alerts, _) =>
        {
            await alerts.CheckAsync(noon, CancellationToken.None);
            return 0;
        });
    }

    private static async Task<List<string>> NotificationTypesAsync(ProjectsApi api, Session session)
    {
        var response = await api.SendAsync(HttpMethod.Get, "/api/notifications?pageSize=50", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("type").GetString()!).ToList();
    }

    private static async Task WaitForNotificationAsync(ProjectsApi api, Session session, string type, int count = 1)
    {
        var timer = Stopwatch.StartNew();
        while ((await NotificationTypesAsync(api, session)).Count(found => found == type) < count)
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"No {type} notification within 30 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    private static Task<List<LocationAlert>> AlertsAsync(ProjectsApi api, Team team) =>
        api.QueryAsync(db => db.LocationAlerts.AsNoTracking().Where(alert => alert.ProjectId == team.ProjectId).OrderBy(alert => alert.Id).ToListAsync());

    [DatabaseFact]
    public async Task Only_the_site_engineer_shares_after_turning_it_on_and_only_the_manager_and_executives_see_it()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;
        var (_, outsider) = await api.CreateSignedInUserAsync(team.Executive, "engineer");

        var mine = await Json(await api.SendAsync(HttpMethod.Get, $"{Base(team)}/me", team.Engineer));
        Assert.True(mine.GetProperty("canShare").GetBoolean());
        Assert.False(mine.GetProperty("isOn").GetBoolean());
        Assert.Equal(5, mine.GetProperty("heartbeatMinutes").GetInt32());

        // Nothing is sent before the engineer turns it on.
        var early = await PositionAsync(api, team, SiteLatitude, SiteLongitude);
        await EnsureStatus(early, HttpStatusCode.Conflict);
        Assert.Equal("location_sharing_off", await CodeOf(early));

        // Only this project's site engineers can turn it on; someone not on the project doesn't see it at all.
        await EnsureStatus(await TurnAsync(api, team, team.Manager, true), HttpStatusCode.Forbidden);
        await EnsureStatus(await TurnAsync(api, team, outsider, true), HttpStatusCode.NotFound);
        await EnsureStatus(await TurnAsync(api, team, team.Engineer, true), HttpStatusCode.OK);

        var atSite = await Json(await PositionAsync(api, team, SiteLatitude + 0.001m, SiteLongitude));
        Assert.True(atSite.GetProperty("stored").GetBoolean());
        Assert.True(atSite.GetProperty("insideSite").GetBoolean());
        Assert.InRange(atSite.GetProperty("distanceMeters").GetInt32(), 100, 120);
        Assert.Equal(300, atSite.GetProperty("nextInSeconds").GetInt32());

        var onMap = await EngineerOnMapAsync(api, team);
        Assert.Equal(team.EngineerId, onMap.GetProperty("userId").GetInt32());
        Assert.Equal("OnSite", onMap.GetProperty("state").GetString());
        Assert.Equal(SiteLatitude + 0.001m, onMap.GetProperty("latitude").GetDecimal());

        // The engineer doesn't see the map; the Executive does, with the day's trail.
        await EnsureStatus(await api.SendAsync(HttpMethod.Get, Base(team), team.Engineer), HttpStatusCode.Forbidden);
        var trail = await api.SendAsync(HttpMethod.Get, $"{Base(team)}/{team.EngineerId}/positions", team.Executive);
        await EnsureStatus(trail, HttpStatusCode.OK);
        Assert.Single((await Json(trail)).GetProperty("items").EnumerateArray());

        var bad = await api.SendAsync(HttpMethod.Post, $"{Base(team)}/me/positions", team.Engineer,
            new { latitude = 91m, longitude = 31m, accuracyMeters = -1, recordedAt = DateTime.UtcNow.AddHours(-2) });
        await EnsureStatus(bad, HttpStatusCode.BadRequest);
        var fields = (await Json(bad)).GetProperty("errors").EnumerateObject().Select(field => field.Name).ToList();
        Assert.Equal(["accuracyMeters", "latitude", "recordedAt"], fields.Order(StringComparer.Ordinal));
    }

    [DatabaseFact]
    public async Task Silence_alerts_the_manager_and_executives_once_and_the_next_position_ends_it()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;
        await EnsureStatus(await TurnAsync(api, team, team.Engineer, true), HttpStatusCode.OK);
        var noon = await WithAlertsAsync(api, (_, clock) => Task.FromResult(YesterdayNoon(clock)));

        await GoSilentAsync(api, team, noon);
        await GoSilentAsync(api, team, noon.AddMinutes(5)); // still silent: no second alert

        var alerts = await AlertsAsync(api, team);
        Assert.Equal(new[] { team.ManagerId, team.Executive.UserId }.Order(), alerts.Select(alert => alert.RecipientUserId).Order());
        Assert.All(alerts, alert => Assert.IsType<LocationUnavailableAlert>(alert));
        Assert.Equal("Unavailable", (await EngineerOnMapAsync(api, team)).GetProperty("state").GetString());
        await WaitForNotificationAsync(api, team.Manager, "LocationUnavailable");
        await WaitForNotificationAsync(api, team.Executive, "LocationUnavailable");

        // A position ends the outage, and both hear it once.
        await EnsureStatus(await PositionAsync(api, team, SiteLatitude, SiteLongitude), HttpStatusCode.OK);

        Assert.All(await AlertsAsync(api, team), alert => Assert.Equal(LocationAlertStatus.Recovered, alert.Status));
        Assert.Equal("OnSite", (await EngineerOnMapAsync(api, team)).GetProperty("state").GetString());
        await WaitForNotificationAsync(api, team.Manager, "LocationAvailable");
        await WaitForNotificationAsync(api, team.Executive, "LocationAvailable");
        Assert.Empty(await NotificationTypesAsync(api, team.Engineer));
    }

    [DatabaseFact]
    public async Task Leaving_the_site_area_alerts_after_two_clear_positions_and_coming_back_ends_it()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;
        await EnsureStatus(await TurnAsync(api, team, team.Engineer, true), HttpStatusCode.OK);

        // About 6.7 km north of the site.
        await EnsureStatus(await PositionAsync(api, team, SiteLatitude + 0.06m, SiteLongitude), HttpStatusCode.OK);
        Assert.Empty(await AlertsAsync(api, team)); // one stray position is not enough

        // A rough position near the edge can't tell either way: it neither counts nor resets.
        var rough = await Json(await PositionAsync(api, team, SiteLatitude + 0.0055m, SiteLongitude, accuracyMeters: 300));
        Assert.Equal(JsonValueKind.Null, rough.GetProperty("insideSite").ValueKind);

        await EnsureStatus(await PositionAsync(api, team, SiteLatitude + 0.06m, SiteLongitude), HttpStatusCode.OK);
        var alerts = await AlertsAsync(api, team);
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, alert => Assert.IsType<OutsideSiteAlert>(alert));
        Assert.Equal("OutsideSite", (await EngineerOnMapAsync(api, team)).GetProperty("state").GetString());
        await WaitForNotificationAsync(api, team.Manager, "OutsideSite");

        // Further positions outside raise nothing new; coming back closes it.
        await EnsureStatus(await PositionAsync(api, team, SiteLatitude + 0.06m, SiteLongitude), HttpStatusCode.OK);
        Assert.Equal(2, (await AlertsAsync(api, team)).Count);
        await EnsureStatus(await PositionAsync(api, team, SiteLatitude, SiteLongitude), HttpStatusCode.OK);
        Assert.All(await AlertsAsync(api, team), alert => Assert.Equal(LocationAlertStatus.Recovered, alert.Status));
        await WaitForNotificationAsync(api, team.Manager, "BackInsideSite");
    }

    [DatabaseFact]
    public async Task Turning_sharing_off_or_leaving_the_project_ends_alerts_without_telling_anyone()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;
        await EnsureStatus(await TurnAsync(api, team, team.Engineer, true), HttpStatusCode.OK);
        var noon = await WithAlertsAsync(api, (_, clock) => Task.FromResult(YesterdayNoon(clock)));
        await GoSilentAsync(api, team, noon);
        await WaitForNotificationAsync(api, team.Manager, "LocationUnavailable");

        var off = await Json(await TurnAsync(api, team, team.Engineer, false));
        Assert.False(off.GetProperty("isOn").GetBoolean());
        Assert.All(await AlertsAsync(api, team), alert => Assert.Equal(LocationAlertStatus.Ended, alert.Status));
        Assert.Equal("Off", (await EngineerOnMapAsync(api, team)).GetProperty("state").GetString());
        await EnsureStatus(await PositionAsync(api, team, SiteLatitude, SiteLongitude), HttpStatusCode.Conflict);

        // On again, then taken off the project: the monitor turns sharing off.
        await EnsureStatus(await TurnAsync(api, team, team.Engineer, true), HttpStatusCode.OK);
        var membership = (await api.MembersAsync(team.Executive, team.ProjectId))
            .Single(member => member.GetProperty("userId").GetInt32() == team.EngineerId);
        await EnsureStatus(await api.SendAsync(HttpMethod.Post,
            $"/api/projects/{team.ProjectId}/members/{membership.GetProperty("memberId").GetInt32()}/end", team.Executive,
            new { reason = "Moved to another site", rowVersion = membership.GetProperty("rowVersion").GetString() }), HttpStatusCode.OK);
        await WithAlertsAsync(api, async (alerts, clock) =>
        {
            await alerts.CheckAsync(clock.UtcNow, CancellationToken.None);
            return 0;
        });

        Assert.False(await api.QueryAsync(db => db.LocationSharings.AnyAsync(sharing => sharing.IsOn)));
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("LocationAvailable", await NotificationTypesAsync(api, team.Manager));
    }

    [DatabaseFact]
    public async Task Positions_older_than_the_kept_days_are_deleted()
    {
        var (api, team) = await StartAsync(database);
        await using var _ = api;
        await EnsureStatus(await TurnAsync(api, team, team.Engineer, true), HttpStatusCode.OK);
        await EnsureStatus(await PositionAsync(api, team, SiteLatitude, SiteLongitude), HttpStatusCode.OK);
        await api.QueryAsync(async db =>
        {
            db.LocationPings.Add(new LocationPing
            {
                ProjectId = team.ProjectId,
                UserId = team.EngineerId,
                RecordedAt = DateTime.UtcNow.AddDays(-91),
                ReceivedAt = DateTime.UtcNow.AddDays(-91),
                Latitude = SiteLatitude,
                Longitude = SiteLongitude,
            });
            return await db.SaveChangesAsync();
        });

        var deleted = await WithAlertsAsync(api, (alerts, clock) => alerts.DeleteOldPositionsAsync(clock.UtcNow, CancellationToken.None));

        Assert.Equal(1, deleted);
        Assert.Equal(1, await api.QueryAsync(db => db.LocationPings.CountAsync()));
    }
}
