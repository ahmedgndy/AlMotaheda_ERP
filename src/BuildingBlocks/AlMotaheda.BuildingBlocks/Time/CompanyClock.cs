using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlMotaheda.BuildingBlocks.Time;

/// <summary>
/// The current moment (UTC) and today's business date in the company's time zone, for every module.
/// "Today" on a daily sheet or a payment is the date at the company, not in UTC.
/// </summary>
/// <remarks>
/// The time zone is the existing setting <c>Projects:TimeZone</c> (default <c>Africa/Cairo</c>): one setting for
/// the whole company, so two modules can never disagree about what day it is. The Projects module validates it
/// at startup.
/// </remarks>
public sealed class CompanyClock
{
    /// <summary>The configuration key of the company time zone (an IANA id).</summary>
    public const string TimeZoneSetting = "Projects:TimeZone";

    public const string DefaultTimeZone = "Africa/Cairo";

    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;

    public CompanyClock(TimeProvider time, IConfiguration configuration)
    {
        _time = time;

        var zoneId = configuration[TimeZoneSetting];
        _zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(zoneId) ? DefaultTimeZone : zoneId);
    }

    public DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _zone).DateTime);

    /// <summary>
    /// The UTC moment the company's day <paramref name="date"/> starts (midnight in the company time zone), for
    /// filtering stored UTC moments by business dates: from <c>StartOfDayUtc(from)</c> to <c>StartOfDayUtc(to + 1 day)</c>.
    /// </summary>
    public DateTime StartOfDayUtc(DateOnly date) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), _zone);
}

public static class CompanyClockRegistration
{
    /// <summary>Registers the shared clock once. Safe to call from every module.</summary>
    public static IServiceCollection AddCompanyClock(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CompanyClock>();
        return services;
    }
}
