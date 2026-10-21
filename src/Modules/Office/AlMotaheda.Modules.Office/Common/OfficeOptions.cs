using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Common;

/// <summary>
/// The office expense policy (<c>Office</c> section). Every value has a safe default, so the module runs without
/// configuration; the owner changes them in appsettings.
/// </summary>
internal sealed class OfficeOptions
{
    public const string SectionName = "Office";

    /// <summary>An expense above this total (EGP) needs a second approval, one of them the Executive's.</summary>
    public decimal RoutineApprovalLimit { get; set; } = 5000m;

    /// <summary>
    /// When true, expenses submitted but not yet posted are taken off the available balance, so the custodian can't
    /// record more than the cash they hold.
    /// </summary>
    public bool ReservePendingExpenses { get; set; } = true;

    /// <summary>The Price Control categories (codes) whose items an office expense may name.</summary>
    public List<string> PriceCategories { get; set; } = ["Internet", "OfficeExpenses", "Maintenance"];

    /// <summary>The largest photo accepted (megabytes). The app compresses receipts far below this.</summary>
    public int ImageMaxMegabytes { get; set; } = 15;

    /// <summary>The largest PDF accepted for quotations and transfer slips (megabytes).</summary>
    public int DocumentMaxMegabytes { get; set; } = 20;

    /// <summary>How many days back an expense date may be. Older purchases need finance to record them.</summary>
    public int MaxExpenseAgeDays { get; set; } = 60;
}

/// <summary>Where office files are kept (<c>Office:Storage</c>): a private folder, never served by a web server.</summary>
internal sealed class OfficeStorageOptions
{
    public const string SectionName = "Office:Storage";

    /// <summary>The folder used outside Production when nothing is set, relative to the content root.</summary>
    public const string DefaultRootPath = "App_Data/office";

    public string? RootPath { get; set; }
}

/// <summary>
/// Fills in the folder when none is set: a sub-folder of the Archive's private folder (already backed up and outside
/// the application folder on servers), otherwise, outside Production, the default folder. A relative folder is made
/// absolute.
/// </summary>
internal sealed class OfficeStorageOptionsSetup(IHostEnvironment environment, IConfiguration configuration) : IPostConfigureOptions<OfficeStorageOptions>
{
    /// <summary>The Archive module's folder setting, read as configuration (the Office module never uses Archive code).</summary>
    public const string ArchiveRootSetting = "Archive:Storage:RootPath";

    public const string ArchiveSubFolder = "office-module";

    public void PostConfigure(string? name, OfficeStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            if (configuration[ArchiveRootSetting] is { } archiveRoot && !string.IsNullOrWhiteSpace(archiveRoot))
            {
                options.RootPath = Path.Combine(Path.GetFullPath(archiveRoot.Trim(), environment.ContentRootPath), ArchiveSubFolder);
            }
            else if (environment.IsProduction())
            {
                return;
            }
            else
            {
                options.RootPath = OfficeStorageOptions.DefaultRootPath;
            }
        }

        options.RootPath = Path.GetFullPath(options.RootPath.Trim(), environment.ContentRootPath);
    }
}

/// <summary>Checked at startup: the folder must be set (in Production), creatable and writable.</summary>
internal sealed class OfficeStorageOptionsValidator : IValidateOptions<OfficeStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, OfficeStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            return ValidateOptionsResult.Fail(
                "Office:Storage:RootPath is missing. Set it to a private folder outside the application folder, for example with the "
                + "Office__Storage__RootPath environment variable.");
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
            return ValidateOptionsResult.Fail($"Office:Storage:RootPath '{options.RootPath}' can't be created or written: {exception.Message}");
        }
    }
}

internal sealed class OfficeOptionsValidator : IValidateOptions<OfficeOptions>
{
    public ValidateOptionsResult Validate(string? name, OfficeOptions options)
    {
        var failures = new List<string>();
        if (options.RoutineApprovalLimit is < 0 or > 10_000_000m)
        {
            failures.Add("Office:RoutineApprovalLimit must be between 0 and 10,000,000.");
        }

        if (options.ImageMaxMegabytes is < 1 or > 100)
        {
            failures.Add("Office:ImageMaxMegabytes must be between 1 and 100.");
        }

        if (options.DocumentMaxMegabytes is < 1 or > 200)
        {
            failures.Add("Office:DocumentMaxMegabytes must be between 1 and 200.");
        }

        if (options.MaxExpenseAgeDays is < 1 or > 3650)
        {
            failures.Add("Office:MaxExpenseAgeDays must be between 1 and 3650.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
