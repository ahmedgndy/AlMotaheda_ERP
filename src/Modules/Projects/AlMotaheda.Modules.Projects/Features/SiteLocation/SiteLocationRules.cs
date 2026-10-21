using AlMotaheda.Modules.Projects.Common;

namespace AlMotaheda.Modules.Projects.Features.SiteLocation;

/// <summary>The location rules, with no database access: distance, working hours, and when a phone counts as silent.</summary>
internal static class SiteLocationRules
{
    private const double EarthRadiusMeters = 6_371_000;

    /// <summary>Great-circle distance in whole meters (haversine). Good to a few meters at site scale.</summary>
    public static int DistanceMeters(decimal fromLatitude, decimal fromLongitude, decimal toLatitude, decimal toLongitude)
    {
        static double Radians(decimal degrees) => (double)degrees * Math.PI / 180;

        var dLat = Radians(toLatitude - fromLatitude);
        var dLng = Radians(toLongitude - fromLongitude);
        var a = Math.Pow(Math.Sin(dLat / 2), 2)
                + Math.Cos(Radians(fromLatitude)) * Math.Cos(Radians(toLatitude)) * Math.Pow(Math.Sin(dLng / 2), 2);
        return (int)Math.Round(2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a))));
    }

    /// <summary>
    /// Inside when the position itself is within the radius; outside only when even the edge of its accuracy circle
    /// nearest the site is beyond it; otherwise unknown (<c>null</c>). A rough position never raises an alert.
    /// </summary>
    public static bool? InsideSite(int distanceMeters, int? accuracyMeters, int radiusMeters) =>
        distanceMeters <= radiusMeters ? true
        : distanceMeters - (accuracyMeters ?? 0) > radiusMeters ? false
        : null;

    /// <summary>Today's working hours at the company (UTC), or <c>null</c> on a day off.</summary>
    public static (DateTime StartUtc, DateTime EndUtc)? WorkingHours(DateTime utcNow, TimeZoneInfo zone, SiteLocationOptions options)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        if (!options.WorkDays.Contains(local.DayOfWeek))
        {
            return null;
        }

        var date = DateOnly.FromDateTime(local);
        return (
            TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(options.Start, DateTimeKind.Unspecified), zone),
            TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(options.End, DateTimeKind.Unspecified), zone));
    }

    /// <summary>True when positions are kept and silence is watched now: always with <see cref="SiteLocationOptions.TrackAllDay"/>.</summary>
    public static bool InWorkingHours(DateTime utcNow, TimeZoneInfo zone, SiteLocationOptions options) =>
        options.TrackAllDay
        || (WorkingHours(utcNow, zone, options) is { } hours && utcNow >= hours.StartUtc && utcNow < hours.EndUtc);

    /// <summary>
    /// Since when the phone has been silent, counting only today's working hours: the last position, or the moment
    /// sharing was turned on, or the start of the working day, whichever is latest. <c>null</c> outside working
    /// hours: nobody is expected to send then.
    /// </summary>
    public static DateTime? SilentSince(DateTime? lastPingAt, DateTime turnedOnAt, DateTime utcNow, TimeZoneInfo zone, SiteLocationOptions options)
    {
        if (options.TrackAllDay)
        {
            // Watched around the clock: silent since the last position, or since tracking started.
            return lastPingAt is { } latest && latest > turnedOnAt ? latest : turnedOnAt;
        }

        if (WorkingHours(utcNow, zone, options) is not { } hours || utcNow < hours.StartUtc || utcNow >= hours.EndUtc)
        {
            return null;
        }

        var since = lastPingAt is { } last && last > turnedOnAt ? last : turnedOnAt;
        return since > hours.StartUtc ? since : hours.StartUtc;
    }

    public static bool IsUnavailable(DateTime? silentSince, DateTime utcNow, SiteLocationOptions options) =>
        silentSince is { } since && utcNow - since >= TimeSpan.FromMinutes(options.UnavailableAfterMinutes);

    /// <summary>
    /// A position or heartbeat is live when it reached the server soon after the phone took it. Only live ones may
    /// open or close alerts, so a queue sent hours later can't raise an alert about the past as if it were now.
    /// </summary>
    public static bool IsLive(DateTime recordedAt, DateTime utcNow, SiteLocationOptions options) =>
        utcNow - recordedAt <= TimeSpan.FromMinutes(options.LiveWindowMinutes);

    /// <summary>
    /// "Outside the site" for one project counts only when the position is clearly outside that site and not inside
    /// another project site of the same engineer: an engineer of two projects at the other site is at work.
    /// </summary>
    public static bool CountsAsOutside(bool? insideThisSite, bool insideAnotherSite) => insideThisSite == false && !insideAnotherSite;

    /// <summary>Low battery: at or below the threshold and not charging.</summary>
    public static bool IsLowBattery(int? percent, bool? charging, SiteLocationOptions options) =>
        percent is { } level && level <= options.LowBatteryPercent && charging != true;

    /// <summary>The low-battery alert ends when the phone charges or climbs back to the recovered level.</summary>
    public static bool IsBatteryRecovered(int? percent, bool? charging, SiteLocationOptions options) =>
        charging == true || (percent is { } level && level >= options.BatteryRecoveredPercent);
}
