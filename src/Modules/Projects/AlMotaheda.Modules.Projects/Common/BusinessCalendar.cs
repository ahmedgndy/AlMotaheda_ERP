using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Settings of the Projects module (section <c>Projects</c>).</summary>
internal sealed class ProjectsOptions
{
    public const string SectionName = "Projects";

    /// <summary>
    /// The company's time zone (IANA id). "Today" for business dates, such as the default start date of a
    /// membership, is the date in this zone, not in UTC.
    /// </summary>
    public string TimeZone { get; set; } = "Africa/Cairo";
}

internal sealed class ProjectsOptionsValidator : IValidateOptions<ProjectsOptions>
{
    public ValidateOptionsResult Validate(string? name, ProjectsOptions options) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(options.TimeZone ?? "", out _)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Projects:TimeZone '{options.TimeZone}' is not a time zone known to this server.");
}

/// <summary>The current moment (UTC) and today's business date in the company's time zone.</summary>
internal sealed class BusinessCalendar(TimeProvider time, IOptions<ProjectsOptions> options)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), _zone).DateTime);
}
