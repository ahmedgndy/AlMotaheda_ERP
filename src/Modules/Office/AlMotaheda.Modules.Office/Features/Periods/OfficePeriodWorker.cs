using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Features.Alerts;
using AlMotaheda.Modules.Office.Features.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Features.Periods;

/// <summary>
/// Starts each month without anyone opening a screen: every <see cref="OfficePeriodOptions.CheckEveryMinutes"/> it
/// opens the new month of every custody and ends the last one (<see cref="CustodyPeriods"/>), then asks Finance for
/// the standing monthly allocations not asked yet (<see cref="PeriodAllocation"/>). Every step is idempotent, so a
/// missed round, a restart or two servers change nothing.
/// </summary>
internal sealed class OfficePeriodWorker(
    IServiceScopeFactory scopes,
    IOptions<OfficeOptions> options,
    OfficeLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup (and its migrations) comes first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A database outage must not stop the cycle; it tries again on the next round.
                log.PeriodCheckFailed(exception);
            }

            await Task.Delay(TimeSpan.FromMinutes(options.Value.Periods.CheckEveryMinutes), stoppingToken);
        }
    }

    /// <summary>One round: open and end months, then ask for the planned allocations. Public for the tests.</summary>
    public static async Task RunOnceAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<OfficeDbContext>();
        var clock = services.GetRequiredService<CompanyClock>();
        var alerts = services.GetRequiredService<OfficeAlerts>();
        var log = services.GetRequiredService<OfficeLog>();
        var settings = services.GetRequiredService<IOptions<OfficeOptions>>().Value;

        var custodies = await db.Custodies.AsNoTracking().Where(custody => custody.Status != OfficeCustodyStatus.Closed)
            .Select(custody => custody.Id).ToListAsync(cancellationToken);
        await CustodyPeriods.RollIfNeededAsync(db, custodies, clock.Today, clock.UtcNow, alerts, log, cancellationToken);

        var notLookedAt = await db.Periods.AsNoTracking()
            .Where(period => period.Status == OfficePeriodStatus.Open && period.AllocationAutoCheckedAt == null)
            .Select(period => new { period.Id, period.CustodyId })
            .ToListAsync(cancellationToken);
        foreach (var period in notLookedAt)
        {
            await AskAllocationAsync(services, db, period.CustodyId, period.Id, settings.Periods.AutoRequestAllocation, clock, alerts, log, cancellationToken);
        }
    }

    /// <summary>
    /// Looks once at a new month's planned allocation: asks Finance for it on behalf of the reviewer who authorized the
    /// standing amount, while that person is still an active Accountant; otherwise tells finance to authorize it by hand.
    /// </summary>
    private static async Task AskAllocationAsync(
        IServiceProvider services, OfficeDbContext db, int custodyId, int periodId, bool automatic, CompanyClock clock, OfficeAlerts alerts, OfficeLog log,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedger.LockAsync(db, custodyId, cancellationToken))
        {
            return;
        }

        var period = await db.Periods.SingleAsync(candidate => candidate.Id == periodId, cancellationToken);
        var custody = await db.Custodies.SingleAsync(candidate => candidate.Id == custodyId, cancellationToken);
        if (period.AllocationAutoCheckedAt is not null || period.Status != OfficePeriodStatus.Open)
        {
            return;
        }

        var now = clock.UtcNow;
        period.AllocationAutoCheckedAt = now;
        string? notAsked = null;
        if (period.PlannedAllocation <= 0)
        {
            notAsked = "no standing monthly allocation";
        }
        else if (!automatic)
        {
            notAsked = "automatic requests are off";
        }
        else if (!custody.IsActive)
        {
            notAsked = $"the custody is {custody.Status}";
        }
        else if (await PeriodAllocation.LiveAsync(db, period.Id, cancellationToken) is not null)
        {
            notAsked = "already authorized";
        }
        else if (custody.MonthlyAllocationSetBy is not { } authorizer
                 || !(await services.GetRequiredService<IUserDirectory>().FindAsync([authorizer], cancellationToken)).TryGetValue(authorizer, out var person)
                 || !person.IsActive || person.RoleCode != AccessRoles.Accountant || authorizer == custody.HolderUserId)
        {
            notAsked = "the person who authorized the standing amount can't ask Finance any more";
        }

        if (notAsked is null)
        {
            var funding = await PeriodAllocation.RequestAsync(db, services.GetRequiredService<IPaymentRequests>(), custody, period, period.PlannedAllocation,
                PaymentMethods.Cash, "Standing monthly allocation", PeriodAllocation.AutomaticKey(period.Id), custody.MonthlyAllocationSetBy!.Value, now,
                clock.Today, cancellationToken);
            log.AllocationAuthorized(funding.RequestedBy, period.Id, funding.Id);
        }
        else
        {
            if (notAsked != "already authorized" && custody.IsActive)
            {
                alerts.Add<OfficePeriodOpenedAlert>(db, await alerts.ReviewersAsync(custody.HolderUserId, cancellationToken), custody.Id, OfficeAlertSources.Period,
                    period.Id, now);
            }

            log.AllocationNotRequested(period.Id, notAsked);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
