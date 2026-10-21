using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Settings of the site engineers' location sharing (section <c>SiteLocation</c>). The defaults are the owner's
/// approved rules of 2026-10-17, updated by the owner's brief of 2026-10-17 (evening): tracking starts by itself on
/// the company phone after the engineer accepts the tracking policy once, and runs all day and night
/// (<see cref="TrackAllDay"/>). Set <c>TrackAllDay</c> to false to go back to the working days and hours below.
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

    /// <summary>
    /// True (default): positions are kept and silence is watched at every hour of every day. False: only during
    /// <see cref="WorkDays"/> between <see cref="WorkStart"/> and <see cref="WorkEnd"/>.
    /// </summary>
    public bool TrackAllDay { get; set; } = true;

    /// <summary>The version of the tracking policy the engineer must accept on their phone. Raise it when the text changes.</summary>
    public int PolicyVersion { get; set; } = 1;

    /// <summary>How often the phone takes a position while it moves (seconds). It also waits for <see cref="MinDistanceMeters"/>.</summary>
    public int SampleSeconds { get; set; } = 60;

    /// <summary>A new position is kept when the phone moved at least this far, or a heartbeat is due.</summary>
    public int MinDistanceMeters { get; set; } = 25;

    /// <summary>
    /// A position counts as live (it can open or close an "outside the site" alert, or end a "location unavailable"
    /// alert) only when it reaches the server within this many minutes of being taken. Older positions, sent late
    /// from the phone's offline queue, go into the history only. Default: <see cref="UnavailableAfterMinutes"/>.
    /// </summary>
    public int? LiveMinutes { get; set; }

    /// <summary>How old a queued position or heartbeat may be when it finally arrives. Older ones are refused.</summary>
    public int MaxLateDays { get; set; } = 7;

    /// <summary>At most this many positions in one upload.</summary>
    public int MaxBatchPoints { get; set; } = 500;

    /// <summary>A live heartbeat at or below this battery level, not charging, raises "battery low" once.</summary>
    public int LowBatteryPercent { get; set; } = 15;

    /// <summary>The "battery low" alert ends at or above this level, or when the phone is charging.</summary>
    public int BatteryRecoveredPercent { get; set; } = 25;

    public int LiveWindowMinutes => LiveMinutes ?? UnavailableAfterMinutes;

    /// <summary>The company's working days (only used when <see cref="TrackAllDay"/> is false). Default Saturday to Thursday.</summary>
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

        if (options.PolicyVersion < 1)
        {
            failures.Add("SiteLocation:PolicyVersion must be 1 or more.");
        }

        if (options.SampleSeconds is < 10 or > 3600)
        {
            failures.Add("SiteLocation:SampleSeconds must be 10 to 3600.");
        }

        if (options.MinDistanceMeters is < 0 or > 5000)
        {
            failures.Add("SiteLocation:MinDistanceMeters must be 0 to 5000.");
        }

        if (options.LiveMinutes is < 1 or > 1440)
        {
            failures.Add("SiteLocation:LiveMinutes must be 1 to 1440.");
        }

        if (options.MaxLateDays < 1 || options.MaxLateDays > options.KeepDays)
        {
            failures.Add("SiteLocation:MaxLateDays must be 1 or more and not more than KeepDays.");
        }

        if (options.MaxBatchPoints is < 1 or > 5000)
        {
            failures.Add("SiteLocation:MaxBatchPoints must be 1 to 5000.");
        }

        if (options.LowBatteryPercent is < 1 or > 99 || options.BatteryRecoveredPercent <= options.LowBatteryPercent || options.BatteryRecoveredPercent > 100)
        {
            failures.Add("SiteLocation:LowBatteryPercent must be 1 to 99 and BatteryRecoveredPercent above it, at most 100.");
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
