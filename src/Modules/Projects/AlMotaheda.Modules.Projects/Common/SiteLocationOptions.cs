using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Settings of the site engineers' location sharing (section <c>SiteLocation</c>). The defaults are the owner's
/// approved rules of 2026-10-17; the working days and hours are a starting guess to confirm.
/// </summary>
internal sealed class SiteLocationOptions
{
    public const string SectionName = "SiteLocation";

    /// <summary>Off: the endpoints still answer, but the monitor raises no alerts and deletes nothing.</summary>
    public bool MonitorEnabled { get; set; } = true;

    /// <summary>How often the app sends a position while sharing is on.</summary>
    public int HeartbeatMinutes { get; set; } = 5;

    /// <summary>No position for this long, during working hours, raises "location unavailable".</summary>
    public int UnavailableAfterMinutes { get; set; } = 15;

    /// <summary>Positions in a row clearly outside the site area before "outside the site" is raised.</summary>
    public int OutsidePingsBeforeAlert { get; set; } = 2;

    /// <summary>Positions older than this are deleted.</summary>
    public int KeepDays { get; set; } = 90;

    /// <summary>How often the monitor looks for silent phones.</summary>
    public int CheckEverySeconds { get; set; } = 60;

    /// <summary>The company's working days. Default Saturday to Thursday.</summary>
    public DayOfWeek[] WorkDays { get; set; } =
        [DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday];

    /// <summary>Start of the working day, company time (<c>HH:mm</c>).</summary>
    public string WorkStart { get; set; } = "08:00";

    /// <summary>End of the working day, company time (<c>HH:mm</c>). After it the app stops sending.</summary>
    public string WorkEnd { get; set; } = "17:00";

    public TimeOnly Start => TimeOnly.Parse(WorkStart, System.Globalization.CultureInfo.InvariantCulture);

    public TimeOnly End => TimeOnly.Parse(WorkEnd, System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class SiteLocationOptionsValidator : IValidateOptions<SiteLocationOptions>
{
    public ValidateOptionsResult Validate(string? name, SiteLocationOptions options)
    {
        var failures = new List<string>();
        if (options.HeartbeatMinutes is < 1 or > 60)
        {
            failures.Add("SiteLocation:HeartbeatMinutes must be 1 to 60.");
        }

        if (options.UnavailableAfterMinutes < options.HeartbeatMinutes * 2)
        {
            failures.Add("SiteLocation:UnavailableAfterMinutes must be at least two heartbeats.");
        }

        if (options.OutsidePingsBeforeAlert is < 1 or > 10)
        {
            failures.Add("SiteLocation:OutsidePingsBeforeAlert must be 1 to 10.");
        }

        if (options.KeepDays is < 1 or > 3650)
        {
            failures.Add("SiteLocation:KeepDays must be 1 to 3650.");
        }

        if (options.CheckEverySeconds is < 5 or > 3600)
        {
            failures.Add("SiteLocation:CheckEverySeconds must be 5 to 3600.");
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!TimeOnly.TryParseExact(options.WorkStart, "HH:mm", culture, System.Globalization.DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(options.WorkEnd, "HH:mm", culture, System.Globalization.DateTimeStyles.None, out var end)
            || end <= start)
        {
            failures.Add("SiteLocation:WorkStart and WorkEnd must be HH:mm, with the end after the start.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
