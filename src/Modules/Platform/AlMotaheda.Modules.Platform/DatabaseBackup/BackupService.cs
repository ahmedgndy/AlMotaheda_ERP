using System.Runtime.InteropServices;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Platform.DatabaseBackup;

/// <summary>Wakes the backup service at once when an Executive asks for a backup now.</summary>
internal sealed class BackupSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _wake.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Runs the daily backup and the "Run backup now" requests. Windows only: elsewhere it logs once and does nothing
/// (the database server, the backup folder and the application share one Windows machine).
/// </summary>
internal sealed class BackupService(
    IServiceScopeFactory scopes, BackupSignal signal, IOptions<BackupOptions> options, ILogger<BackupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ServiceEnabled)
        {
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !options.Value.AllowNonWindows)
        {
            logger.LogInformation("Database backups run on Windows only; this server is not Windows, so none will run");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "The backup check failed; trying again later");
            }

            await signal.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)), stoppingToken);
        }
    }

    /// <summary>Runs a requested backup, or the daily one when it is due.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<CompanyClock>();
        var runner = scope.ServiceProvider.GetRequiredService<BackupRunner>();

        var settings = await db.BackupSettings.SingleOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            return;
        }

        if (settings.RunRequestedAt is not null)
        {
            var requestedBy = settings.RunRequestedBy;
            settings.RunRequestedAt = null;
            settings.RunRequestedBy = null;
            await db.SaveChangesAsync(cancellationToken);
            await runner.RunAsync(BackupTrigger.Manual, requestedBy, cancellationToken);
            return;
        }

        var lastScheduled = await db.BackupRuns.AsNoTracking()
            .Where(run => run.Trigger == BackupTrigger.Scheduled)
            .OrderByDescending(run => run.StartedAt)
            .Select(run => (DateTime?)run.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, clock.Zone);
        var lastLocal = lastScheduled is { } last ? TimeZoneInfo.ConvertTimeFromUtc(last, clock.Zone) : (DateTime?)null;

        if (BackupPlan.IsDue(settings, nowLocal, lastLocal))
        {
            await runner.RunAsync(BackupTrigger.Scheduled, null, cancellationToken);
        }
    }
}
