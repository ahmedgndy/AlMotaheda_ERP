using System.Globalization;
using System.Text.RegularExpressions;

namespace AlMotaheda.Modules.Platform.DatabaseBackup;

/// <summary>The backup rules, with no database or disk access (18-backup-changes.md).</summary>
internal static partial class BackupPlan
{
    /// <summary><c>{Database}_Full_{yyyyMMdd_HHmmss}.bak</c>: the only names this service ever writes or deletes.</summary>
    public static string BackupFileName(string database, DateTime atLocal) =>
        $"{database}_Full_{atLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.bak";

    /// <summary><c>{Database}_Files_{yyyyMMdd_HHmmss}.zip</c>: the uploaded documents of the same run.</summary>
    public static string DocumentsFileName(string database, DateTime atLocal) =>
        $"{database}_Files_{atLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.zip";

    /// <summary>True only for a file name this service could have written for <paramref name="database"/>.</summary>
    public static bool IsServiceFile(string fileName, string database) =>
        ServiceFile().Match(fileName) is { Success: true } match && match.Groups["db"].Value == database;

    [GeneratedRegex(@"^(?<db>[A-Za-z0-9_\-]+)_(Full|Files)_\d{8}_\d{6}\.(bak|zip)$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceFile();

    /// <summary>
    /// Is the daily backup due? Once per company day, at or after <see cref="BackupSettings.DailyAt"/>, unless a
    /// scheduled run already started that day. A "Run now" request is handled apart.
    /// </summary>
    public static bool IsDue(BackupSettings settings, DateTime nowLocal, DateTime? lastScheduledStartLocal) =>
        settings.Enabled
        && TimeOnly.FromDateTime(nowLocal) >= settings.DailyAt
        && (lastScheduledStartLocal is null || DateOnly.FromDateTime(lastScheduledStartLocal.Value) < DateOnly.FromDateTime(nowLocal));

    /// <summary>
    /// The runs whose files retention removes: the verified ones beyond the newest <paramref name="keep"/>, oldest
    /// first. Only called after a new backup was verified, so the newest always stays. Failed or unverified runs are
    /// never counted as "kept" and their partial files are left for a person to look at.
    /// </summary>
    public static IReadOnlyList<BackupRun> ToRemove(IEnumerable<BackupRun> runs, int keep) =>
        runs.Where(run => run.Status == BackupStatus.Succeeded && run.Verified && run.FilesDeletedAt is null)
            .OrderByDescending(run => run.StartedAt)
            .ThenByDescending(run => run.Id)
            .Skip(Math.Max(1, keep))
            .ToList();
}
