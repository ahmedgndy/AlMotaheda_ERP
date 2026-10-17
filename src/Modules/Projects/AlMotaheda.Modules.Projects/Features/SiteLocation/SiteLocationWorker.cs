using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Features.SiteLocation;

/// <summary>
/// Runs the location monitor every <see cref="SiteLocationOptions.CheckEverySeconds"/>, and deletes old positions
/// once an hour. The first round waits one interval, so startup (and its migrations) comes first.
/// </summary>
internal sealed class SiteLocationWorker(
    IServiceScopeFactory scopes,
    CompanyClock clock,
    IOptions<SiteLocationOptions> options,
    ProjectsLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.MonitorEnabled)
        {
            return;
        }

        var nextCleanup = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.Value.CheckEverySeconds), stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var alerts = scope.ServiceProvider.GetRequiredService<SiteLocationAlerts>();
                var now = clock.UtcNow;
                await alerts.CheckAsync(now, stoppingToken);
                if (now >= nextCleanup)
                {
                    await alerts.DeleteOldPositionsAsync(now, stoppingToken);
                    nextCleanup = now.AddHours(1);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A database outage must not stop the monitor; it tries again on the next round.
                log.LocationMonitorFailed(exception);
            }
        }
    }
}
