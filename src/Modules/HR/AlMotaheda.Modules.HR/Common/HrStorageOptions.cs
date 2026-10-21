using AlMotaheda.BuildingBlocks.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.HR.Common;

/// <summary>Where HR request evidence files are kept (<c>Hr:Storage</c>): a private folder, never served by a web server.</summary>
internal sealed class HrStorageOptions
{
    public const string SectionName = "Hr:Storage";

    /// <summary>The folder used outside Production when nothing is set, relative to the content root.</summary>
    public const string DefaultRootPath = "App_Data/hr";

    public const string ArchiveSubFolder = "hr-module";

    /// <summary>The largest file accepted (megabytes).</summary>
    public int MaxMegabytes { get; set; } = 15;

    public string? RootPath { get; set; }

    /// <summary>True when the folder follows the Archive module's (<see cref="PrivateFolder"/>): that module checks it at startup.</summary>
    public bool FollowsArchive { get; set; }
}

internal sealed class HrStorageOptionsSetup(IHostEnvironment environment, IConfiguration configuration) : IPostConfigureOptions<HrStorageOptions>
{
    public void PostConfigure(string? name, HrStorageOptions options)
    {
        var resolved = PrivateFolder.Resolve(options.RootPath, HrStorageOptions.ArchiveSubFolder, HrStorageOptions.DefaultRootPath, environment.ContentRootPath,
            environment.IsProduction(), configuration[PrivateFolder.ArchiveRootSetting]);
        options.RootPath = resolved.RootPath;
        options.FollowsArchive = resolved.FollowsArchive;
    }
}

internal sealed class HrStorageOptionsValidator : IValidateOptions<HrStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, HrStorageOptions options)
    {
        if (options.MaxMegabytes is < 1 or > 100)
        {
            return ValidateOptionsResult.Fail("Hr:Storage:MaxMegabytes must be between 1 and 100.");
        }

        return PrivateFolder.Check("Hr:Storage:RootPath", new PrivateFolder.Resolution(options.RootPath, options.FollowsArchive)) is { } failure
            ? ValidateOptionsResult.Fail(failure)
            : ValidateOptionsResult.Success;
    }
}
