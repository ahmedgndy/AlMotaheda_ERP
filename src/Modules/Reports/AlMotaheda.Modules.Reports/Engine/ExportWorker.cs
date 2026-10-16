using System.Security.Claims;
using System.Text.Json;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Reports.Common;
using AlMotaheda.Modules.Reports.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Reports.Engine;

/// <summary>
/// Builds large Excel exports in the background, one at a time, with the requester's rights as they were when they
/// asked. A finished run is Ready (the Platform notifies the requester) or Failed. Expired files are removed; the
/// run itself stays as the record of the export.
/// </summary>
internal sealed class ExportWorker(IServiceScopeFactory scopes, IOptions<ReportsOptions> options, ReportsLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundExports)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await RunNextAsync(stoppingToken))
                {
                }

                await PurgeExpiredAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A database outage must not stop the worker; it tries again on the next round.
                log.ExportFailed(0, "-", exception);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)), stoppingToken);
        }
    }

    /// <summary>Runs the oldest queued export. False when the queue is empty.</summary>
    internal async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ReportsDbContext>();
        var clock = services.GetRequiredService<CompanyClock>();

        var run = await db.Runs.Where(candidate => candidate.Status == ReportRunStatus.Queued).OrderBy(candidate => candidate.Id).FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            return false;
        }

        run.Start();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return true; // another worker took it
        }

        try
        {
            var caller = Principal(run.Claims);
            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(run.Filters) ?? [];
            var runner = services.GetRequiredService<ReportRunner>();
            var outcome = await runner.RunAsync(run.ReportCode, values, caller, cancellationToken);
            if (!outcome.Succeeded)
            {
                run.Fail(outcome.Forbidden ? "You no longer have the permission for this report." : "The report or its filters are no longer valid.", clock.UtcNow);
            }
            else
            {
                var document = await runner.DocumentAsync(outcome, run.Language, run.RequestedBy, cancellationToken);
                run.Complete(outcome.Data!.Rows.Count, document.FileName, ExcelReport.Write(document), clock.UtcNow,
                    clock.UtcNow.AddDays(Math.Max(1, options.Value.KeepExportsDays)));
                log.ExportReady(run.Id, run.ReportCode, outcome.Data.Rows.Count);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.ExportFailed(run.Id, run.ReportCode, exception);
            run.Fail("The export could not be built. Try again or narrow the filters.", clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportsDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<CompanyClock>().UtcNow;

        var expired = await db.Runs.Where(run => run.Content != null && run.ExpiresAt <= now).Take(50).ToListAsync(cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        foreach (var run in expired)
        {
            run.Content = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static ClaimsPrincipal Principal(string? claims)
    {
        var pairs = claims is null ? [] : JsonSerializer.Deserialize<string[][]>(claims) ?? [];
        return new ClaimsPrincipal(new ClaimsIdentity(pairs.Select(pair => new Claim(pair[0], pair[1])), "report-export"));
    }
}
