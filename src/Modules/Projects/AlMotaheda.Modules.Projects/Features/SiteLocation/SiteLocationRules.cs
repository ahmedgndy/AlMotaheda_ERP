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

    public static bool InWorkingHours(DateTime utcNow, TimeZoneInfo zone, SiteLocationOptions options) =>
        WorkingHours(utcNow, zone, options) is { } hours && utcNow >= hours.StartUtc && utcNow < hours.EndUtc;

    /// <summary>
    /// Since when the phone has been silent, counting only today's working hours: the last position, or the moment
    /// sharing was turned on, or the start of the working day, whichever is latest. <c>null</c> outside working
    /// hours: nobody is expected to send then.
    /// </summary>
    public static DateTime? SilentSince(DateTime? lastPingAt, DateTime turnedOnAt, DateTime utcNow, TimeZoneInfo zone, SiteLocationOptions options)
    {
        if (WorkingHours(utcNow, zone, options) is not { } hours || utcNow < hours.StartUtc || utcNow >= hours.EndUtc)
        {
            return null;
        }

        var since = lastPingAt is { } last && last > turnedOnAt ? last : turnedOnAt;
        return since > hours.StartUtc ? since : hours.StartUtc;
    }

    public static bool IsUnavailable(DateTime? silentSince, DateTime utcNow, SiteLocationOptions options) =>
        silentSince is { } since && utcNow - since >= TimeSpan.FromMinutes(options.UnavailableAfterMinutes);
}
