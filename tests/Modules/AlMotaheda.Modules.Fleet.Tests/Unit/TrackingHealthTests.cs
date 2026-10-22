using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Gps;

namespace AlMotaheda.Modules.Fleet.Tests.Unit;

/// <summary>
/// Tracking health from evidence only (L, M, N): a stale vehicle is shown with its real last time and no invented
/// reason; a condition is named only when the phone reported it after its last position. And which queued
/// positions are refused for good, with what reason (K).
/// </summary>
public sealed class TrackingHealthTests
{
    private static readonly VehicleGpsOptions Settings = new();
    private static readonly DateTime Now = new(2026, 10, 21, 10, 0, 0, DateTimeKind.Utc);

    private static VehicleTrackingDevice Phone(DateTime? heartbeat = null, string? state = "Running", string? permission = "Always", bool? gps = true) => new()
    {
        UserId = 7, DeviceId = "phone-1", Platform = "Android", PolicyVersion = 1, PolicyAcceptedAt = Now.AddDays(-3),
        LastHeartbeatAt = heartbeat, TrackingState = state, Permission = permission, GpsEnabled = gps,
    };

    private static GpsHealthView Classify(VehicleTrackingDevice? device, DateTime? lastPoint, bool enabled = true, bool driver = true) =>
        TrackingHealth.Classify(enabled, driver, device, lastPoint, Now, Settings);

    [Fact]
    public void Fresh_delayed_and_stale_follow_the_age_of_the_last_position()
    {
        Assert.Equal("Live", Classify(Phone(), Now.AddMinutes(-2)).State);
        Assert.Equal("Delayed", Classify(Phone(), Now.AddMinutes(-10)).State);

        // N: stale, shown with its real time and age, and no claim about why.
        var stale = Classify(Phone(heartbeat: Now.AddHours(-4)), Now.AddHours(-3));
        Assert.Equal(("Offline", "Server", Now.AddHours(-3), 180), (stale.State, stale.Source, stale.LastPointAt, stale.MinutesSinceLastPoint));
        Assert.Contains("unknown", stale.Detail);
    }

    [Fact]
    public void A_condition_the_phone_reported_after_its_last_position_is_named_as_the_phones()
    {
        var lastPoint = Now.AddHours(-1);
        Assert.Equal(("PermissionBlocked", "Device"), Pair(Classify(Phone(Now.AddMinutes(-30), "PermissionDenied", "Denied"), lastPoint)));
        Assert.Equal(("PermissionBlocked", "Device"), Pair(Classify(Phone(Now.AddMinutes(-30), "BackgroundDenied", "WhileInUse"), lastPoint)));
        Assert.Equal(("GpsOff", "Device"), Pair(Classify(Phone(Now.AddMinutes(-30), "GpsOff", gps: false), lastPoint)));
        Assert.Equal(("TrackingStopped", "Device"), Pair(Classify(Phone(Now.AddMinutes(-30), "Stopped"), lastPoint)));

        // The phone reports, but no position comes: no fix (it says so), not "offline".
        Assert.Equal(("NoFix", "Device"), Pair(Classify(Phone(Now.AddMinutes(-2)), lastPoint)));

        // A complaint from before the last position explains nothing about the silence after it.
        Assert.Equal("Offline", Classify(Phone(Now.AddHours(-2), "GpsOff", gps: false), lastPoint).State);

        static (string, string) Pair(GpsHealthView view) => (view.State, view.Source);
    }

    [Fact]
    public void Nothing_is_called_live_without_a_driver_a_phone_or_tracking()
    {
        Assert.Equal("NotTracked", Classify(Phone(), Now, enabled: false).State);
        Assert.Equal("NoDriver", Classify(Phone(), Now, driver: false).State);
        Assert.Equal("NotSetUp", Classify(null, null).State);
        Assert.Equal("NoData", Classify(Phone(), null).State);
    }

    [Theory]
    [InlineData(null, 30.0, 31.0, 8, 0, "id")]
    [InlineData("not-a-guid", 30.0, 31.0, 8, 0, "id")]
    [InlineData("00000000-0000-0000-0000-000000000000", 30.0, 31.0, 8, 0, "id")]
    [InlineData("8d6f0a8e-3a1b-4d9e-9f51-1d1f7b6a5c01", 91.0, 31.0, 8, 0, "position")]
    [InlineData("8d6f0a8e-3a1b-4d9e-9f51-1d1f7b6a5c01", 30.0, -181.0, 8, 0, "position")]
    [InlineData("8d6f0a8e-3a1b-4d9e-9f51-1d1f7b6a5c01", 0.0, 0.0, 8, 0, "position")]
    [InlineData("8d6f0a8e-3a1b-4d9e-9f51-1d1f7b6a5c01", 30.0, 31.0, -1, 0, "accuracy")]
    [InlineData("8d6f0a8e-3a1b-4d9e-9f51-1d1f7b6a5c01", 30.0, 31.0, 8, 10, "future")]
    [InlineData("8d6f0a8e-3a1b-4d9e-9f51-1d1f7b6a5c01", 30.0, 31.0, 8, -60 * 24 * 15, "tooOld")]
    [InlineData("8d6f0a8e-3a1b-4d9e-9f51-1d1f7b6a5c01", 30.0, 31.0, 8, -60 * 24 * 13, null)]
    [InlineData("8d6f0a8e-3a1b-4d9e-9f51-1d1f7b6a5c01", 30.0, 31.0, 8, 1, null)]
    public void K_a_queued_position_is_refused_for_good_only_for_a_stated_reason(string? id, double latitude, double longitude, int accuracy, int minutesFromNow, string? reason)
    {
        var point = new VehiclePointRequest(id, Now.AddMinutes(minutesFromNow), (decimal)latitude, (decimal)longitude, accuracy, null, null, null, null, null);

        // A valid position two weeks late is still accepted: only the configured limit (14 days) refuses it.
        Assert.Equal(reason, DriverTracking.Reject(point, Now, Now.AddDays(-Settings.MaxLateDays)));
    }
}
