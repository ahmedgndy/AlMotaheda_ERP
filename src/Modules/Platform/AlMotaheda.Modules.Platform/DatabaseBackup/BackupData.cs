namespace AlMotaheda.Modules.Platform.DatabaseBackup;

/// <summary>
/// The backup settings: one row, changed only by the Executive (every change is written to the audit trail).
/// </summary>
internal sealed class BackupSettings
{
    public const int SingletonId = 1;

    /// <summary>The folder the settings start with (seeded); the Executive changes it to the server's backup disk.</summary>
    public const string DefaultFolder = @"C:\AlMotaheda\Backups";

    public int Id { get; set; } = SingletonId;

    public bool Enabled { get; set; } = true;

    /// <summary>Company time of day the daily backup starts.</summary>
    public TimeOnly DailyAt { get; set; } = new(2, 0);

    /// <summary>A local folder on the database server (the application runs on the same Windows machine).</summary>
    public string Folder { get; set; } = DefaultFolder;

    /// <summary>How many verified backups to keep (the brief's default: 3).</summary>
    public int RetentionCount { get; set; } = 3;

    /// <summary>Also zip the uploaded documents (the Archive folder) next to the database backup.</summary>
    public bool IncludeDocuments { get; set; } = true;

    /// <summary>Set by "Run backup now"; the service runs it within a minute (at once when it is awake).</summary>
    public DateTime? RunRequestedAt { get; set; }
    public int? RunRequestedBy { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One backup: when, what, how big, verified or not, and why it failed.</summary>
internal sealed class BackupRun
{
    public long Id { get; set; }

    public BackupTrigger Trigger { get; set; }

    public int? RequestedBy { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public BackupStatus Status { get; set; } = BackupStatus.Running;

    public string Database { get; set; } = "";

    /// <summary>The full path of the .bak file, as SQL Server wrote it.</summary>
    public string? FilePath { get; set; }

    public long? SizeBytes { get; set; }

    /// <summary><c>RESTORE VERIFYONLY ... WITH CHECKSUM</c> passed.</summary>
    public bool Verified { get; set; }

    public bool Compressed { get; set; }

    public string? DocumentsFilePath { get; set; }
    public long? DocumentsSizeBytes { get; set; }

    public string? Error { get; set; }

    /// <summary>Set when retention removed this backup's files (only after a newer one was verified).</summary>
    public DateTime? FilesDeletedAt { get; set; }
}

/// <summary>Stored by name.</summary>
internal enum BackupTrigger
{
    Scheduled,
    Manual,
}

/// <summary>Stored by name.</summary>
internal enum BackupStatus
{
    Running,
    Succeeded,
    Failed,
}
