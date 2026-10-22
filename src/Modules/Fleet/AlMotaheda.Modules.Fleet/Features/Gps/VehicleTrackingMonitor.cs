using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.HR.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Features.Gps;

/// <summary>
/// Opens and closes the "vehicle stopped reporting" alerts, and the monitor's round. The alerts are ordinary audited
/// records, so the Platform module turns them into notifications (and pushes) after the save, like any other record.
/// </summary>
/// <remarks>
/// A vehicle is <em>expected</em> to report when tracking is on for it, it is in service or under maintenance, it
/// has an open driver assignment, and that driver accepted the current tracking notice on a phone. Only an expected
/// vehicle can go silent. One alert per recipient per silence: the next round never opens a second one, and the
/// first live position closes it (Recovered, which notifies once). When the vehicle is no longer expected the alert
/// closes as Ended, without a notification.
/// </remarks>
internal sealed class VehicleTrackingAlerts(
    FleetDbContext db,
    IUserDirectory users,
    IEmployeeDirectory employees,
    CompanyClock clock,
    IOptions<FleetOptions> options,
    FleetLog log)
{
    private VehicleGpsOptions Settings => options.Value.Gps;

    /// <summary>Who hears about a silent vehicle: every active Executive, never the driver.</summary>
    public async Task<IReadOnlyList<int>> RecipientsAsync(int? driverUserId, CancellationToken cancellationToken) =>
        (await users.ListActiveAsync([AccessRoles.Executive], cancellationToken))
            .Select(user => user.Id)
            .Where(id => id != driverUserId)
            .Distinct()
            .Order()
            .ToList();

    /// <summary>Adds an open alert for each recipient without one. Saved by the caller. Returns how many are told.</summary>
    public async Task<int> OpenAsync(int vehicleId, int? driverEmployeeId, int? driverUserId, DateTime? lastPointAt, DateTime now, CancellationToken cancellationToken)
    {
        var recipients = await RecipientsAsync(driverUserId, cancellationToken);
        var open = await db.VehicleTrackingAlerts
            .Where(alert => alert.VehicleId == vehicleId && alert.Status == VehicleTrackingAlertStatus.Open)
            .Select(alert => alert.RecipientUserId)
            .ToListAsync(cancellationToken);
        foreach (var recipient in recipients.Except(open))
        {
            db.VehicleTrackingAlerts.Add(new VehicleTrackingAlert
            {
                VehicleId = vehicleId,
                DriverEmployeeId = driverEmployeeId,
                RecipientUserId = recipient,
                Status = VehicleTrackingAlertStatus.Open,
                LastPointAt = lastPointAt,
                StartedAt = now,
            });
        }

        return recipients.Count;
    }

    /// <summary>Closes the open alerts about a vehicle. Saved by the caller.</summary>
    public async Task CloseAsync(int vehicleId, VehicleTrackingAlertStatus status, DateTime now, CancellationToken cancellationToken)
    {
        var open = await db.VehicleTrackingAlerts
            .Where(alert => alert.VehicleId == vehicleId && alert.Status == VehicleTrackingAlertStatus.Open)
            .ToListAsync(cancellationToken);
        foreach (var alert in open)
        {
            alert.Status = status;
            alert.EndedAt = now;
        }
    }

    /// <summary>The expected vehicles now: vehicle, open assignment, the driver's user and their latest accepted phone.</summary>
    public async Task<IReadOnlyList<ExpectedVehicle>> ExpectedAsync(CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var rows = await db.VehicleDriverAssignments.AsNoTracking()
            .Where(assignment => assignment.EndDate == null && assignment.StartDate <= today
                                 && assignment.Vehicle.TrackingEnabled
                                 && (assignment.Vehicle.Equipment.Status == EquipmentStatus.InService || assignment.Vehicle.Equipment.Status == EquipmentStatus.UnderMaintenance))
            .Select(assignment => new { assignment.Id, assignment.VehicleId, assignment.DriverEmployeeId, assignment.StartDate, assignment.AssignedAt })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var drivers = await employees.FindAsync(rows.Select(row => row.DriverEmployeeId).Distinct().ToList(), cancellationToken);
        var userIds = drivers.Values.Where(driver => driver.IsActive && driver.UserId != null).Select(driver => driver.UserId!.Value).Distinct().ToList();
        var policy = Settings.PolicyVersion;
        var devices = await db.VehicleTrackingDevices.AsNoTracking()
            .Where(device => userIds.Contains(device.UserId) && device.PolicyVersion >= policy)
            .ToListAsync(cancellationToken);

        var expected = new List<ExpectedVehicle>();
        foreach (var row in rows)
        {
            if (!drivers.TryGetValue(row.DriverEmployeeId, out var driver) || !driver.IsActive || driver.UserId is not { } userId)
            {
                continue;
            }

            var device = devices.Where(candidate => candidate.UserId == userId).OrderByDescending(candidate => candidate.PolicyAcceptedAt).FirstOrDefault();
            if (device is null)
            {
                continue;
            }

            // Tracking is expected from the later of: the notice accepted, the assignment made, its first day.
            var since = new[] { device.PolicyAcceptedAt, row.AssignedAt, clock.StartOfDayUtc(row.StartDate) }.Max();
            expected.Add(new ExpectedVehicle(row.VehicleId, row.Id, row.DriverEmployeeId, userId, since));
        }

        return expected;
    }

    /// <summary>
    /// The monitor's round: an expected vehicle silent for longer than <see cref="VehicleGpsOptions.StaleMinutes"/>
    /// alerts once; a vehicle no longer expected closes its alerts without a notification.
    /// </summary>
    public async Task CheckAsync(DateTime now, CancellationToken cancellationToken)
    {
        var expected = await ExpectedAsync(cancellationToken);
        var expectedIds = expected.Select(vehicle => vehicle.VehicleId).ToHashSet();
        var states = await db.VehicleTrackingStates.ToListAsync(cancellationToken);

        foreach (var vehicle in expected)
        {
            var state = states.FirstOrDefault(row => row.VehicleId == vehicle.VehicleId);
            if (state is null)
            {
                state = new VehicleTrackingState { VehicleId = vehicle.VehicleId };
                db.VehicleTrackingStates.Add(state);
                states.Add(state);
            }

            if (state.SilentSince is not null)
            {
                continue;
            }

            var silentSince = state.LastPointAt is { } last && last > vehicle.ExpectedSince ? last : vehicle.ExpectedSince;
            if (now - silentSince <= TimeSpan.FromMinutes(Settings.StaleMinutes))
            {
                continue;
            }

            state.SilentSince = silentSince;
            var told = await OpenAsync(vehicle.VehicleId, vehicle.DriverEmployeeId, vehicle.DriverUserId, state.LastPointAt, now, cancellationToken);
            log.VehicleSilent(vehicle.VehicleId, told);
        }

        foreach (var state in states.Where(row => row.SilentSince is not null && !expectedIds.Contains(row.VehicleId)))
        {
            state.SilentSince = null;
            await CloseAsync(state.VehicleId, VehicleTrackingAlertStatus.Ended, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Deletes positions older than the retention period, in chunks.</summary>
    public async Task DeleteOldPositionsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-Settings.RetentionDays);
        var total = 0;
        while (true)
        {
            var deleted = await db.VehicleGpsPoints.Where(point => point.RecordedAt < cutoff).OrderBy(point => point.Id).Take(5000).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
            if (deleted < 5000)
            {
                break;
            }
        }

        if (total > 0)
        {
            log.OldPositionsDeleted(total, Settings.RetentionDays);
        }
    }
}

/// <summary>A vehicle whose driver's phone should be sending positions, since <paramref name="ExpectedSince"/>.</summary>
internal sealed record ExpectedVehicle(int VehicleId, int AssignmentId, int DriverEmployeeId, int DriverUserId, DateTime ExpectedSince);

/// <summary>
/// Runs the vehicle tracking monitor every <see cref="VehicleGpsOptions.CheckEverySeconds"/> and deletes old
/// positions once an hour. The first round waits one interval, so startup (and its migrations) comes first.
/// </summary>
internal sealed class VehicleTrackingWorker(IServiceScopeFactory scopes, CompanyClock clock, IOptions<FleetOptions> options, FleetLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Gps.MonitorEnabled)
        {
            return;
        }

        var nextCleanup = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.Value.Gps.CheckEverySeconds), stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var alerts = scope.ServiceProvider.GetRequiredService<VehicleTrackingAlerts>();
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
                log.TrackingMonitorFailed(exception);
            }
        }
    }
}
