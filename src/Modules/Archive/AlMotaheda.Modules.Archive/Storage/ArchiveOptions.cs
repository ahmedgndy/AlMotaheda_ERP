using AlMotaheda.Modules.Archive.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Archive.Storage;

/// <summary>Settings of the file store (<c>Archive:Storage</c>).</summary>
internal sealed class ArchiveStorageOptions
{
    public const string SectionName = "Archive:Storage";

    /// <summary>The folder used when <see cref="RootPath"/> is not set, relative to the content root. Not in Production.</summary>
    public const string DefaultRootPath = "App_Data/archive";

    /// <summary>
    /// The folder that holds every project's files. Must be outside any folder a web server serves, and on
    /// storage that is backed up. A relative path is taken from the application's content root.
    /// </summary>
    public string? RootPath { get; set; }
}

/// <summary>
/// Fills in the default folder outside Production, and makes a relative folder absolute. In Production the folder
/// must be set, so the files never land inside the application folder by accident (a redeploy would wipe them).
/// </summary>
internal sealed class ArchiveStorageOptionsSetup(IHostEnvironment environment) : IPostConfigureOptions<ArchiveStorageOptions>
{
    public void PostConfigure(string? name, ArchiveStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            if (environment.IsProduction())
            {
                return;
            }

            options.RootPath = ArchiveStorageOptions.DefaultRootPath;
        }

        options.RootPath = Path.GetFullPath(options.RootPath.Trim(), environment.ContentRootPath);
    }
}

/// <summary>
/// Checked at startup (<c>ValidateOnStart</c>): the folder must exist or be creatable, and be writable. A bad
/// setting stops the application at startup, not at the first upload.
/// </summary>
internal sealed class ArchiveStorageOptionsValidator : IValidateOptions<ArchiveStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, ArchiveStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            return ValidateOptionsResult.Fail(
                "Archive:Storage:RootPath is missing. Set it to a folder outside the application folder, for example with the "
                + "Archive__Storage__RootPath environment variable.");
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
            return ValidateOptionsResult.Fail(
                $"Archive:Storage:RootPath '{options.RootPath}' can't be created or written: {exception.Message}");
        }
    }
}

/// <summary>The largest file accepted per kind (<c>Archive:Limits</c>), in megabytes.</summary>
internal sealed class ArchiveLimitsOptions
{
    public const string SectionName = "Archive:Limits";

    private const long Megabyte = 1024 * 1024;

    /// <summary>Extra room for the form fields and the multipart framing around the file.</summary>
    private const long FormOverheadBytes = Megabyte;

    public int ImageMaxMegabytes { get; set; } = 25;

    /// <summary>PDF, office files and drawings.</summary>
    public int DocumentMaxMegabytes { get; set; } = 50;

    public int VideoMaxMegabytes { get; set; } = 500;

    public long MaxBytesFor(DocumentKind kind) => kind switch
    {
        DocumentKind.Image => ImageMaxMegabytes * Megabyte,
        DocumentKind.Video => VideoMaxMegabytes * Megabyte,
        _ => DocumentMaxMegabytes * Megabyte,
    };

    /// <summary>The request size limit of the upload endpoint: the largest file plus the form around it.</summary>
    public long MaxRequestBytes =>
        Math.Max(ImageMaxMegabytes, Math.Max(DocumentMaxMegabytes, VideoMaxMegabytes)) * Megabyte + FormOverheadBytes;
}

internal sealed class ArchiveLimitsOptionsValidator : IValidateOptions<ArchiveLimitsOptions>
{
    /// <summary>10 GB: far above any site video, low enough that a typing mistake is caught.</summary>
    private const int MaxMegabytes = 10_240;

    public ValidateOptionsResult Validate(string? name, ArchiveLimitsOptions options)
    {
        var failures = new List<string>();
        Check(options.ImageMaxMegabytes, nameof(options.ImageMaxMegabytes), failures);
        Check(options.DocumentMaxMegabytes, nameof(options.DocumentMaxMegabytes), failures);
        Check(options.VideoMaxMegabytes, nameof(options.VideoMaxMegabytes), failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(int value, string setting, List<string> failures)
    {
        if (value is < 1 or > MaxMegabytes)
        {
            failures.Add($"Archive:Limits:{setting} must be between 1 and {MaxMegabytes}.");
        }
    }
}
