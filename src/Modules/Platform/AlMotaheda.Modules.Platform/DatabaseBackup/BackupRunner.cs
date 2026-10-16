using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Platform.Data;
using AlMotaheda.Modules.Platform.Live;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Platform.DatabaseBackup;

/// <summary>
/// Takes one backup: <c>BACKUP DATABASE ... WITH CHECKSUM</c> (and <c>COMPRESSION</c> when the edition supports it),
/// then <c>RESTORE VERIFYONLY ... WITH CHECKSUM</c>, then zips the uploaded documents, then removes the oldest
/// backups beyond the retention count: only after the new one is verified, and only files this service named.
/// A failure is recorded and every active Executive is notified.
/// </summary>
internal sealed class BackupRunner(
    PlatformDbContext db,
    IBackupFiles files,
    IUserDirectory users,
    ILiveUpdates live,
    CompanyClock clock,
    IConfiguration configuration,
    IOptions<BackupOptions> options,
    ILogger<BackupRunner> logger)
{
    private const string BackupCompressed = "BACKUP DATABASE @database TO DISK = @path WITH CHECKSUM, INIT, FORMAT, COMPRESSION, NAME = @name";
    private const string BackupUncompressed = "BACKUP DATABASE @database TO DISK = @path WITH CHECKSUM, INIT, FORMAT, NAME = @name";

    /// <summary>One backup at a time on this server.</summary>
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<BackupRun?> RunAsync(BackupTrigger trigger, int? requestedBy, CancellationToken cancellationToken)
    {
        if (!await OneAtATime.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        try
        {
            return await RunOnceAsync(trigger, requestedBy, cancellationToken);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private async Task<BackupRun> RunOnceAsync(BackupTrigger trigger, int? requestedBy, CancellationToken cancellationToken)
    {
        var settings = await db.BackupSettings.AsNoTracking().SingleAsync(cancellationToken);
        var database = new SqlConnectionStringBuilder(db.Database.GetConnectionString()).InitialCatalog;
        var startedLocal = TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, clock.Zone);
        var run = new BackupRun { Trigger = trigger, RequestedBy = requestedBy, StartedAt = clock.UtcNow, Database = database };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            files.EnsureFolder(settings.Folder);
            var path = Path.Combine(settings.Folder, BackupPlan.BackupFileName(database, startedLocal));
            run.FilePath = path;
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(Math.Max(1, options.Value.CommandTimeoutMinutes)));

            // Express edition can't compress; every other edition can.
            run.Compressed = await db.Database
                .SqlQueryRaw<int>("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int) AS [Value]")
                .SingleAsync(cancellationToken) != 4;

            // Names and paths are parameters, never text in the statement.
            await db.Database.ExecuteSqlRawAsync(
                run.Compressed ? BackupCompressed : BackupUncompressed,
                [new SqlParameter("@database", database), new SqlParameter("@path", path), new SqlParameter("@name", $"AlMotaheda {trigger} backup")],
                cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM",
                [new SqlParameter("@path", path)],
                cancellationToken);
            run.Verified = true;

            run.SizeBytes = await db.Database
                .SqlQueryRaw<long>(
                    """
                    SELECT TOP (1) CAST(b.compressed_backup_size AS bigint) AS [Value]
                    FROM msdb.dbo.backupset AS b JOIN msdb.dbo.backupmediafamily AS m ON m.media_set_id = b.media_set_id
                    WHERE m.physical_device_name = @path ORDER BY b.backup_finish_date DESC
                    """,
                    new SqlParameter("@path", path))
                .SingleOrDefaultAsync(cancellationToken);

            if (settings.IncludeDocuments && configuration["Archive:Storage:RootPath"] is { Length: > 0 } documents)
            {
                var zip = Path.Combine(settings.Folder, BackupPlan.DocumentsFileName(database, startedLocal));
                if (files.ZipFolder(documents, zip) is { } size)
                {
                    run.DocumentsFilePath = zip;
                    run.DocumentsSizeBytes = size;
                }
            }

            run.Status = BackupStatus.Succeeded;
            run.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await ApplyRetentionAsync(settings.RetentionCount, database, cancellationToken);
            return run;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Backup {RunId} of {Database} failed", run.Id, database);
            run.Status = BackupStatus.Failed;
            run.Error = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
            run.CompletedAt = clock.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            await NotifyFailureAsync(run, CancellationToken.None);
            return run;
        }
    }

    /// <summary>Removes the oldest verified backups beyond the count. Never a file whose name this service didn't write.</summary>
    private async Task ApplyRetentionAsync(int keep, string database, CancellationToken cancellationToken)
    {
        var runs = await db.BackupRuns.Where(run => run.Database == database && run.FilesDeletedAt == null).ToListAsync(cancellationToken);
        foreach (var old in BackupPlan.ToRemove(runs, keep))
        {
            foreach (var path in new[] { old.FilePath, old.DocumentsFilePath })
            {
                if (path is not null && BackupPlan.IsServiceFile(Path.GetFileName(path), database))
                {
                    files.Delete(path);
                }
            }

            old.FilesDeletedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task NotifyFailureAsync(BackupRun run, CancellationToken cancellationToken)
    {
        try
        {
            var executives = await users.ListActiveAsync([AccessRoles.Executive], cancellationToken);
            var source = Guid.NewGuid();
            var notifications = executives.Select(executive => new Notification
            {
                RecipientUserId = executive.Id,
                Type = BackupNotifications.BackupFailed,
                TitleAr = "فشل النسخ الاحتياطي لقاعدة البيانات",
                TitleEn = "The database backup failed",
                EntityType = nameof(BackupRun),
                EntityKey = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceMessageId = source,
                CreatedAt = clock.UtcNow,
            }).ToList();
            db.Notifications.AddRange(notifications);
            await db.SaveChangesAsync(cancellationToken);

            await live.PublishAsync(notifications.Select(notification => new LiveMessage(
                LiveScopes.User(notification.RecipientUserId),
                LiveMethods.Notification,
                new NotificationSignal(notification.Id, notification.Type, notification.EntityType, notification.EntityKey, null))).ToList(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The Executives could not be notified that backup {RunId} failed", run.Id);
        }
    }
}

internal static class BackupNotifications
{
    /// <summary>A notification type, stored by name and returned to the client.</summary>
    public const string BackupFailed = "BackupFailed";
}
