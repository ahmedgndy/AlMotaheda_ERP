using System.IO.Compression;

namespace AlMotaheda.Modules.Platform.DatabaseBackup;

/// <summary>Settings of the backup service (<c>Platform:Backup</c>).</summary>
internal sealed class BackupOptions
{
    public const string SectionName = "Platform:Backup";

    /// <summary>Off only for test hosts without a database. The Executive's on/off is the setting in the database.</summary>
    public bool ServiceEnabled { get; set; } = true;

    /// <summary>How often the service checks whether a backup is due.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Upper limit for one BACKUP or RESTORE VERIFYONLY statement.</summary>
    public int CommandTimeoutMinutes { get; set; } = 120;

    /// <summary>Tests only: run outside Windows (the service otherwise logs and skips).</summary>
    public bool AllowNonWindows { get; set; }
}

/// <summary>The backup folder on the server's disk. A test replaces it when SQL Server runs elsewhere (Docker).</summary>
internal interface IBackupFiles
{
    /// <summary>Creates the folder if needed. Throws when it can't.</summary>
    void EnsureFolder(string folder);

    /// <summary>Zips <paramref name="sourceFolder"/> into <paramref name="zipPath"/>; returns its size, or null when there is no such folder.</summary>
    long? ZipFolder(string sourceFolder, string zipPath);

    void Delete(string path);
}

internal sealed class LocalBackupFiles : IBackupFiles
{
    public void EnsureFolder(string folder) => Directory.CreateDirectory(folder);

    public long? ZipFolder(string sourceFolder, string zipPath)
    {
        if (!Directory.Exists(sourceFolder))
        {
            return null;
        }

        ZipFile.CreateFromDirectory(sourceFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return new FileInfo(zipPath).Length;
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
