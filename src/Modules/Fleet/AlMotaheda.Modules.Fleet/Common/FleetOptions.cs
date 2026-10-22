using AlMotaheda.BuildingBlocks.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Common;

/// <summary>The vehicle policies (<c>Fleet</c> section). Every value has a safe default.</summary>
internal sealed class FleetOptions
{
    public const string SectionName = "Fleet";

    /// <summary>A claim above this amount (EGP) is approved by an Executive only; 0 = the accountant approves every claim.</summary>
    public decimal ClaimExecutiveApprovalAbove { get; set; }

    /// <summary>A claim whose odometer reading is this many kilometres or more above the last reading, within one day, is flagged as implausible.</summary>
    public decimal ImplausibleDistanceKmPerDay { get; set; } = 1_000m;

    /// <summary>How many days back a claim's expense date may be.</summary>
    public int MaxClaimAgeDays { get; set; } = 60;

    /// <summary>The largest photo accepted (megabytes).</summary>
    public int ImageMaxMegabytes { get; set; } = 15;

    /// <summary>The largest PDF accepted (megabytes).</summary>
    public int DocumentMaxMegabytes { get; set; } = 20;

    public VehicleGpsOptions Gps { get; set; } = new();
}

/// <summary>
/// The vehicle GPS settings (<c>Fleet:Gps</c>). Positions come from the phone of the vehicle's assigned driver
/// (the app's tracking service), after the driver accepts the tracking notice on it; no external tracker is
/// integrated. Every threshold of sampling, stop detection, trip segmentation and staleness lives here, so none is
/// scattered through the code; the phone receives its part from <c>GET /api/fleet/tracking/me</c>.
/// </summary>
internal sealed class VehicleGpsOptions
{
    /// <summary>The name the status endpoint reports for the driver's phone as the position source.</summary>
    public const string DriverPhone = "DriverPhone";

    /// <summary>The source of positions. Only <see cref="DriverPhone"/> is implemented.</summary>
    public string Provider { get; set; } = DriverPhone;

    /// <summary>The version of the tracking notice drivers accept. Raising it asks every driver to accept again.</summary>
    public int PolicyVersion { get; set; } = 1;

    /// <summary>How often the phone takes a position while tracking (seconds). The target is one per minute.</summary>
    public int SampleSeconds { get; set; } = 60;

    /// <summary>
    /// The phone's distance filter (metres). 0 keeps one position per <see cref="SampleSeconds"/> even when the
    /// vehicle is parked, so a stop is backed by samples; raising it saves battery but leaves stops without samples.
    /// </summary>
    public int MinDistanceMeters { get; set; }

    /// <summary>How often the phone reports its own state (battery, permission, GPS on/off).</summary>
    public int HeartbeatMinutes { get; set; } = 5;

    /// <summary>A position less accurate than this (metres) is kept but never used for routes, trips or stops.</summary>
    public int MaxAccuracyMeters { get; set; } = 100;

    /// <summary>Positions within this distance (metres, widened by their accuracy) of each other are "not moving".</summary>
    public int StopRadiusMeters { get; set; } = 50;

    /// <summary>Not moving for this long confirms a stop (minutes); shorter halts stay part of the trip.</summary>
    public int StopMinutes { get; set; } = 5;

    /// <summary>A movement shorter than this (metres) between two stops is GPS drift, not a trip.</summary>
    public int MinTripMeters { get; set; } = 300;

    /// <summary>No accepted position for longer than this (minutes) is a data gap: the route is not drawn across it.</summary>
    public int GapMinutes { get; set; } = 10;

    /// <summary>A jump implying more than this speed (km/h) from the previous accepted position is implausible.</summary>
    public int MaxSpeedKmh { get; set; } = 180;

    /// <summary>The last position is "live" when the phone took it at most this many minutes ago.</summary>
    public int LiveMinutes { get; set; } = 3;

    /// <summary>After this many minutes without a position, an expected vehicle is stale and fleet managers are told once.</summary>
    public int StaleMinutes { get; set; } = 20;

    /// <summary>How old (days) a queued position may be when it finally arrives. Older ones are refused with a reason.</summary>
    public int MaxLateDays { get; set; } = 14;

    /// <summary>The most positions one upload may carry.</summary>
    public int MaxBatchPoints { get; set; } = 500;

    /// <summary>How long positions are kept (days); older ones are deleted by the monitor.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Whether the monitor (stale alerts, retention) runs in this process.</summary>
    public bool MonitorEnabled { get; set; } = true;

    public int CheckEverySeconds { get; set; } = 60;
}

internal sealed class FleetOptionsValidator : IValidateOptions<FleetOptions>
{
    public ValidateOptionsResult Validate(string? name, FleetOptions options)
    {
        var failures = new List<string>();
        if (options.ClaimExecutiveApprovalAbove is < 0 or > 10_000_000m)
        {
            failures.Add("Fleet:ClaimExecutiveApprovalAbove must be between 0 and 10,000,000.");
        }

        if (options.ImplausibleDistanceKmPerDay is < 1 or > 100_000)
        {
            failures.Add("Fleet:ImplausibleDistanceKmPerDay must be between 1 and 100,000.");
        }

        if (options.MaxClaimAgeDays is < 1 or > 366)
        {
            failures.Add("Fleet:MaxClaimAgeDays must be between 1 and 366.");
        }

        if (options.ImageMaxMegabytes is < 1 or > 100 || options.DocumentMaxMegabytes is < 1 or > 100)
        {
            failures.Add("Fleet:ImageMaxMegabytes and Fleet:DocumentMaxMegabytes must be between 1 and 100.");
        }

        var gps = options.Gps;
        if (gps.Provider != VehicleGpsOptions.DriverPhone)
        {
            failures.Add($"Fleet:Gps:Provider must be {VehicleGpsOptions.DriverPhone}: no other position source is integrated.");
        }

        void Range(int value, int min, int max, string name)
        {
            if (value < min || value > max)
            {
                failures.Add($"Fleet:Gps:{name} must be between {min} and {max}.");
            }
        }

        Range(gps.PolicyVersion, 1, 1000, nameof(gps.PolicyVersion));
        Range(gps.SampleSeconds, 10, 600, nameof(gps.SampleSeconds));
        Range(gps.MinDistanceMeters, 0, 1000, nameof(gps.MinDistanceMeters));
        Range(gps.HeartbeatMinutes, 1, 60, nameof(gps.HeartbeatMinutes));
        Range(gps.MaxAccuracyMeters, 5, 2000, nameof(gps.MaxAccuracyMeters));
        Range(gps.StopRadiusMeters, 5, 1000, nameof(gps.StopRadiusMeters));
        Range(gps.StopMinutes, 1, 120, nameof(gps.StopMinutes));
        Range(gps.MinTripMeters, 0, 10_000, nameof(gps.MinTripMeters));
        Range(gps.GapMinutes, 2, 240, nameof(gps.GapMinutes));
        Range(gps.MaxSpeedKmh, 30, 400, nameof(gps.MaxSpeedKmh));
        Range(gps.LiveMinutes, 1, 60, nameof(gps.LiveMinutes));
        Range(gps.StaleMinutes, 2, 1440, nameof(gps.StaleMinutes));
        Range(gps.MaxLateDays, 1, 90, nameof(gps.MaxLateDays));
        Range(gps.MaxBatchPoints, 1, 2000, nameof(gps.MaxBatchPoints));
        Range(gps.RetentionDays, 1, 3650, nameof(gps.RetentionDays));
        Range(gps.CheckEverySeconds, 10, 3600, nameof(gps.CheckEverySeconds));
        if (gps.LiveMinutes >= gps.StaleMinutes)
        {
            failures.Add("Fleet:Gps:LiveMinutes must be below Fleet:Gps:StaleMinutes.");
        }

        if (gps.SampleSeconds >= gps.GapMinutes * 60)
        {
            failures.Add("Fleet:Gps:SampleSeconds must be shorter than Fleet:Gps:GapMinutes, or every sample would be a gap.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Where vehicle evidence files are kept (<c>Fleet:Storage</c>): a private folder, never served by a web server.</summary>
internal sealed class FleetStorageOptions
{
    public const string SectionName = "Fleet:Storage";

    /// <summary>The folder used outside Production when nothing is set, relative to the content root.</summary>
    public const string DefaultRootPath = "App_Data/fleet";

    public const string ArchiveSubFolder = "fleet-module";

    public string? RootPath { get; set; }

    /// <summary>True when the folder follows the Archive module's (<see cref="PrivateFolder"/>): that module checks it at startup.</summary>
    public bool FollowsArchive { get; set; }
}

/// <summary>Fills in the folder when none is set (<see cref="PrivateFolder.Resolve"/>).</summary>
internal sealed class FleetStorageOptionsSetup(IHostEnvironment environment, IConfiguration configuration) : IPostConfigureOptions<FleetStorageOptions>
{
    public void PostConfigure(string? name, FleetStorageOptions options)
    {
        var resolved = PrivateFolder.Resolve(options.RootPath, FleetStorageOptions.ArchiveSubFolder, FleetStorageOptions.DefaultRootPath, environment.ContentRootPath,
            environment.IsProduction(), configuration[PrivateFolder.ArchiveRootSetting]);
        options.RootPath = resolved.RootPath;
        options.FollowsArchive = resolved.FollowsArchive;
    }
}

/// <summary>Checked at startup when the folder is set here: it must be creatable and writable (<see cref="PrivateFolder.Check"/>).</summary>
internal sealed class FleetStorageOptionsValidator : IValidateOptions<FleetStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, FleetStorageOptions options) =>
        PrivateFolder.Check("Fleet:Storage:RootPath", new PrivateFolder.Resolution(options.RootPath, options.FollowsArchive)) is { } failure
            ? ValidateOptionsResult.Fail(failure)
            : ValidateOptionsResult.Success;
}
