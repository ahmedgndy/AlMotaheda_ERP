using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Features.SiteLocation;

namespace AlMotaheda.Modules.Projects.Tests.Unit;

/// <summary>Distance, the site area, working hours and silence, without a database.</summary>
public sealed class SiteLocationRulesTests
{
    private static readonly TimeZoneInfo Cairo = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
    private static readonly SiteLocationOptions Defaults = new();

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
    public void Working_hours_are_saturday_to_thursday_eight_to_five_company_time_by_default()
    {
        // 2026-10-17 is a Saturday; 2026-10-16 a Friday.
        Assert.True(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 8, 0), Cairo, Defaults));
        Assert.True(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 16, 59), Cairo, Defaults));
        Assert.False(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 17, 0), Cairo, Defaults));
        Assert.False(SiteLocationRules.InWorkingHours(At(2026, 10, 17, 7, 59), Cairo, Defaults));
        Assert.False(SiteLocationRules.InWorkingHours(At(2026, 10, 16, 10, 0), Cairo, Defaults));
    }

    [Fact]
    public void Silence_counts_from_the_last_position_but_never_from_before_today_s_start()
    {
        var turnedOn = At(2026, 10, 15, 9, 0);
        var lastPing = At(2026, 10, 15, 16, 50); // Thursday, just before going home

        // Saturday 08:10: silent only since 08:00, not since Thursday.
        var since = SiteLocationRules.SilentSince(lastPing, turnedOn, At(2026, 10, 17, 8, 10), Cairo, Defaults);
        Assert.Equal(At(2026, 10, 17, 8, 0), since);
        Assert.False(SiteLocationRules.IsUnavailable(since, At(2026, 10, 17, 8, 10), Defaults));
        Assert.True(SiteLocationRules.IsUnavailable(since, At(2026, 10, 17, 8, 15), Defaults));

        // During the day: from the last position, or from turning it on when nothing was sent yet.
        var today = At(2026, 10, 17, 10, 0);
        Assert.Equal(today, SiteLocationRules.SilentSince(today, turnedOn, At(2026, 10, 17, 10, 14), Cairo, Defaults));
        Assert.Equal(today, SiteLocationRules.SilentSince(null, today, At(2026, 10, 17, 10, 14), Cairo, Defaults));

        // After hours and on a day off nobody is expected to send.
        Assert.Null(SiteLocationRules.SilentSince(lastPing, turnedOn, At(2026, 10, 17, 18, 0), Cairo, Defaults));
        Assert.Null(SiteLocationRules.SilentSince(lastPing, turnedOn, At(2026, 10, 16, 10, 0), Cairo, Defaults));
    }

    [Fact]
    public void Settings_that_make_no_sense_stop_the_server_at_startup()
    {
        var validator = new SiteLocationOptionsValidator();

        Assert.True(validator.Validate(null, new SiteLocationOptions()).Succeeded);
        Assert.True(validator.Validate(null, new SiteLocationOptions { HeartbeatMinutes = 10, UnavailableAfterMinutes = 15 }).Failed);
        Assert.True(validator.Validate(null, new SiteLocationOptions { WorkStart = "17:00", WorkEnd = "08:00" }).Failed);
        Assert.True(validator.Validate(null, new SiteLocationOptions { WorkStart = "8am" }).Failed);
    }
}
