using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Features.SiteLocation;

namespace AlMotaheda.Modules.Projects.Tests.Unit;

/// <summary>Distance, the site area, tracking hours, silence, late positions and battery, without a database.</summary>
public sealed class SiteLocationRulesTests
{
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
    private static readonly SiteLocationOptions Defaults = new();

    /// <summary>The earlier rule: working days and hours only.</summary>
    private static readonly SiteLocationOptions WorkingHoursOnly = new() { TrackAllDay = false };

    /// <summary>A company-time moment as UTC.</summary>
    private static DateTime At(int year, int month, int day, int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified), Cairo);

    [Fact]
    public void Distance_is_in_meters_on_the_ground()
    {
        // 0.001 degree of latitude is about 111 m; the same point is 0.
        Assert.InRange(SiteLocationRules.DistanceMeters(30.1395m, 31.7085m, 30.1405m, 31.7085m), 110, 112);
        Assert.Equal(0, SiteLocationRules.DistanceMeters(30.1395m, 31.7085m, 30.1395m, 31.7085m));
        // Badr City to Tahrir Square, Cairo: about 47 km.
        Assert.InRange(SiteLocationRules.DistanceMeters(30.1395m, 31.7085m, 30.0444m, 31.2357m), 46_000, 48_000);
    }

    [Theory]
    [InlineData(400, 50, true)]
    [InlineData(500, 0, true)]
    [InlineData(560, 50, false)]
    [InlineData(540, 50, null)] // the accuracy circle reaches the area: can't tell
    [InlineData(5000, null, false)]
    public void Outside_only_when_even_the_accuracy_circle_is_beyond_the_radius(int distance, int? accuracy, bool? inside) =>
        Assert.Equal(inside, SiteLocationRules.InsideSite(distance, accuracy, 500));

    [Fact]
    public void Working_hours_are_saturday_to_thursday_eight_to_five_company_time_when_all_day_tracking_is_off()
    {
        // 2026-10-17 is a Saturday; 2026-10-16 a Friday.
        Assert.True(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 8, 0), Cairo, WorkingHoursOnly));
        Assert.True(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 16, 59), Cairo, WorkingHoursOnly));
        Assert.False(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 17, 0), Cairo, WorkingHoursOnly));
        Assert.False(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 7, 59), Cairo, WorkingHoursOnly));
        Assert.False(SiteLocationRules.InWorkingHours(At(2026, 10, 16, 10, 0), Cairo, WorkingHoursOnly));
    }

    [Fact]
    public void By_default_tracking_runs_all_day_and_night_every_day()
    {
        Assert.True(Defaults.TrackAllDay);
        Assert.True(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 3, 0), Cairo, Defaults));
        Assert.True(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 23, 30), Cairo, Defaults));
        Assert.True(SiteLocationRules.InWorkingHours(At(2026, 10, 16, 10, 0), Cairo, Defaults)); // Friday
    }

    [Fact]
    public void All_day_silence_counts_from_the_last_position_even_at_night()
    {
        var turnedOn = At(2026, 10, 15, 9, 0);
        var lastPing = At(2026, 10, 17, 2, 0);

        var since = SiteLocationRules.SilentSince(lastPing, turnedOn, At(2026, 10, 17, 2, 20), Cairo, Defaults);
        Assert.Equal(lastPing, since);
        Assert.True(SiteLocationRules.IsUnavailable(since, At(2026, 10, 17, 2, 20), Defaults));
        Assert.False(SiteLocationRules.IsUnavailable(since, At(2026, 10, 17, 2, 14), Defaults));

        // Nothing sent since tracking started: silent since it started.
        Assert.Equal(turnedOn, SiteLocationRules.SilentSince(null, turnedOn, At(2026, 10, 15, 9, 30), Cairo, Defaults));
    }

    [Fact]
    public void Only_positions_that_arrive_soon_after_they_were_taken_are_live()
    {
        var now = At(2026, 10, 17, 12, 0);
        Assert.True(SiteLocationRules.IsLive(now.AddMinutes(-1), now, Defaults));
        Assert.True(SiteLocationRules.IsLive(now.AddMinutes(-15), now, Defaults));
        Assert.False(SiteLocationRules.IsLive(now.AddMinutes(-16), now, Defaults));
        Assert.False(SiteLocationRules.IsLive(now.AddHours(-3), now, Defaults));
        Assert.True(SiteLocationRules.IsLive(now.AddMinutes(-40), now, new SiteLocationOptions { LiveMinutes = 60 }));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)] // at another of their sites: at work
    [InlineData(null, false, false)] // too rough to tell
    [InlineData(true, false, false)]
    public void Outside_counts_only_when_clearly_outside_and_not_at_another_of_the_engineer_s_sites(bool? insideThis, bool insideAnother, bool outside) =>
        Assert.Equal(outside, SiteLocationRules.CountsAsOutside(insideThis, insideAnother));

    [Theory]
    [InlineData(15, false, true)]
    [InlineData(10, null, true)]
    [InlineData(10, true, false)] // charging
    [InlineData(16, false, false)]
    [InlineData(null, false, false)] // the phone couldn't tell: nothing is guessed
    public void Low_battery_is_at_or_below_the_level_and_not_charging(int? percent, bool? charging, bool low) =>
        Assert.Equal(low, SiteLocationRules.IsLowBattery(percent, charging, Defaults));

    [Theory]
    [InlineData(25, false, true)]
    [InlineData(20, false, false)]
    [InlineData(5, true, true)]
    [InlineData(null, null, false)]
    public void The_battery_alert_ends_when_charging_or_back_at_the_recovered_level(int? percent, bool? charging, bool recovered) =>
        Assert.Equal(recovered, SiteLocationRules.IsBatteryRecovered(percent, charging, Defaults));

    [Fact]
    public void Silence_counts_from_the_last_position_but_never_from_before_today_s_start()
    {
        var turnedOn = At(2026, 10, 15, 9, 0);
        var lastPing = At(2026, 10, 15, 16, 50); // Thursday, just before going home

        // Saturday 08:10: silent only since 08:00, not since Thursday.
        var since = SiteLocationRules.SilentSince(lastPing, turnedOn, At(2026, 10, 17, 8, 10), Cairo, WorkingHoursOnly);
        Assert.Equal(At(2026, 10, 17, 8, 0), since);
        Assert.False(SiteLocationRules.IsUnavailable(since, At(2026, 10, 17, 8, 10), WorkingHoursOnly));
        Assert.True(SiteLocationRules.IsUnavailable(since, At(2026, 10, 17, 8, 15), WorkingHoursOnly));

        // During the day: from the last position, or from turning it on when nothing was sent yet.
        var today = At(2026, 10, 17, 10, 0);
        Assert.Equal(today, SiteLocationRules.SilentSince(today, turnedOn, At(2026, 10, 17, 10, 14), Cairo, WorkingHoursOnly));
        Assert.Equal(today, SiteLocationRules.SilentSince(null, today, At(2026, 10, 17, 10, 14), Cairo, WorkingHoursOnly));

        // After hours and on a day off nobody is expected to send.
        Assert.Null(SiteLocationRules.SilentSince(lastPing, turnedOn, At(2026, 10, 17, 18, 0), Cairo, WorkingHoursOnly));
        Assert.Null(SiteLocationRules.SilentSince(lastPing, turnedOn, At(2026, 10, 16, 10, 0), Cairo, WorkingHoursOnly));
    }

    [Fact]
    public void Settings_that_make_no_sense_stop_the_server_at_startup()
    {
        var validator = new SiteLocationOptionsValidator();

        Assert.True(validator.Validate(null, new SiteLocationOptions()).Succeeded);
        Assert.True(validator.Validate(null, new SiteLocationOptions { HeartbeatMinutes = 10, UnavailableAfterMinutes = 15 }).Failed);
        Assert.True(validator.Validate(null, new SiteLocationOptions { WorkStart = "17:00", WorkEnd = "08:00" }).Failed);
        Assert.True(validator.Validate(null, new SiteLocationOptions { WorkStart = "8am" }).Failed);
        Assert.True(validator.Validate(null, new SiteLocationOptions { MaxLateDays = 100, KeepDays = 90 }).Failed);
        Assert.True(validator.Validate(null, new SiteLocationOptions { LowBatteryPercent = 30, BatteryRecoveredPercent = 20 }).Failed);
        Assert.True(validator.Validate(null, new SiteLocationOptions { SampleSeconds = 1 }).Failed);
        Assert.True(validator.Validate(null, new SiteLocationOptions { PolicyVersion = 0 }).Failed);
    }
}
