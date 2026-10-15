using AlMotaheda.BuildingBlocks.Time;
using Microsoft.Extensions.Configuration;

namespace AlMotaheda.Modules.Archive.Common;

/// <summary>
/// Turns business dates into UTC moments, so "uploaded on 15 January" means 15 January at the company, not in UTC.
/// </summary>
/// <remarks>Uses the company time zone setting that <see cref="CompanyClock"/> uses, so the two always agree.</remarks>
internal sealed class ArchiveCalendar(IConfiguration configuration)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(
        string.IsNullOrWhiteSpace(configuration[CompanyClock.TimeZoneSetting])
            ? CompanyClock.DefaultTimeZone
            : configuration[CompanyClock.TimeZoneSetting]!);

    /// <summary>The UTC moment the company's day <paramref name="date"/> starts.</summary>
    public DateTime StartOfDayUtc(DateOnly date) => StartOfDayUtc(date, _zone);

    /// <summary>
    /// Midnight in <paramref name="zone"/>, as UTC. Where the clocks jump forward at midnight (Egypt starts summer
    /// time at 00:00), midnight doesn't exist; the day then starts at the first minute that does.
    /// </summary>
    public static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
