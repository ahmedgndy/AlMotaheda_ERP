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
/// The vehicle GPS settings (<c>Fleet:Gps</c>). <see cref="Provider"/> names the tracking provider; <c>None</c>
/// (the default) means no provider is integrated and the location endpoints answer so, truthfully. No provider
/// is shipped yet: the integration is the <c>IVehicleLocationProvider</c> abstraction.
/// </summary>
internal sealed class VehicleGpsOptions
{
    public const string None = "None";

    public string Provider { get; set; } = None;

    /// <summary>How long a provider's location history is kept once one exists (days).</summary>
    public int RetentionDays { get; set; } = 90;
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

        if (string.IsNullOrWhiteSpace(options.Gps.Provider))
        {
            failures.Add("Fleet:Gps:Provider must be None or the name of a configured provider.");
        }

        if (options.Gps.RetentionDays is < 1 or > 3650)
        {
            failures.Add("Fleet:Gps:RetentionDays must be between 1 and 3650.");
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

    public string? RootPath { get; set; }

    /// <summary>
    /// True when nothing was set here and the folder follows the Archive module's (<c>Archive:Storage:RootPath</c>):
    /// that module checks the folder at startup and reports it when missing or unwritable, so Fleet doesn't repeat
    /// (or mask) its message; the sub-folder is created on the first upload.
    /// </summary>
    public bool FollowsArchive { get; set; }
}

/// <summary>
/// Fills in the folder when none is set: a sub-folder of the Archive's private folder (already backed up and outside
/// the application folder on servers), otherwise, outside Production, the default folder.
/// </summary>
internal sealed class FleetStorageOptionsSetup(IHostEnvironment environment, IConfiguration configuration) : IPostConfigureOptions<FleetStorageOptions>
{
    /// <summary>The Archive module's folder setting, read as configuration (Fleet never uses Archive code).</summary>
    public const string ArchiveRootSetting = "Archive:Storage:RootPath";

    public const string ArchiveSubFolder = "fleet-module";

    public void PostConfigure(string? name, FleetStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            if (configuration[ArchiveRootSetting] is { } archiveRoot && !string.IsNullOrWhiteSpace(archiveRoot))
            {
                options.RootPath = Path.Combine(Path.GetFullPath(archiveRoot.Trim(), environment.ContentRootPath), ArchiveSubFolder);
                options.FollowsArchive = true;
            }
            else if (environment.IsProduction())
            {
                // The Archive module refuses to start without its folder and says so; one message is enough.
                options.FollowsArchive = true;
                return;
            }
            else
            {
                options.RootPath = FleetStorageOptions.DefaultRootPath;
            }
        }

        options.RootPath = Path.GetFullPath(options.RootPath.Trim(), environment.ContentRootPath);
    }
}

/// <summary>
/// Checked at startup when the folder is set here: it must be creatable and writable. A folder that follows the
/// Archive's is the Archive module's to check.
/// </summary>
internal sealed class FleetStorageOptionsValidator : IValidateOptions<FleetStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, FleetStorageOptions options)
    {
        if (options.FollowsArchive)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            return ValidateOptionsResult.Fail(
                "Fleet:Storage:RootPath is missing. Set it to a private folder outside the application folder, for example with the "
                + "Fleet__Storage__RootPath environment variable (or set Archive__Storage__RootPath: a sub-folder of it is used).");
        }

        try
        {
            Directory.CreateDirectory(options.RootPath);
            var probe = Path.Combine(options.RootPath, $".startup-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            return ValidateOptionsResult.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ValidateOptionsResult.Fail($"Fleet:Storage:RootPath '{options.RootPath}' can't be created or written: {exception.Message}");
        }
    }
}
