using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Features.SiteLocation;

/// <summary>
/// Opens and closes location alerts, and the monitor's round: silent phones, sharing that must stop, old positions.
/// The alerts are ordinary audited records, so the Platform module turns them into notifications (and pushes)
/// after the save, like any other record.
/// </summary>
internal sealed class SiteLocationAlerts(
    ProjectsDbContext db,
    IUserDirectory users,
    CompanyClock clock,
    IOptions<SiteLocationOptions> options,
    ProjectsLog log)
{
    private SiteLocationOptions Options => options.Value;

    /// <summary>Who hears about an engineer: the project's active manager and every active Executive, never the engineer.</summary>
    public async Task<IReadOnlyList<int>> RecipientsAsync(int projectId, int engineerUserId, CancellationToken cancellationToken)
    {
        var managers = await db.ProjectMembers
            .AsNoTracking()
            .Where(member => member.ProjectId == projectId && member.EndDate == null && member.Responsibility == MemberResponsibility.ProjectManager)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);
        var executives = await users.ListActiveAsync([AccessRoles.Executive], cancellationToken);

        return managers
            .Concat(executives.Select(executive => executive.Id))
            .Where(userId => userId != engineerUserId)
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>Adds an open alert for each recipient who doesn't have one yet. Saved by the caller. Returns how many are told.</summary>
    public async Task<int> OpenAsync<TAlert>(int projectId, int engineerUserId, DateTime now, CancellationToken cancellationToken)
        where TAlert : LocationAlert, new()
    {
        var recipients = await RecipientsAsync(projectId, engineerUserId, cancellationToken);
        var alreadyOpen = await db.LocationAlerts
            .OfType<TAlert>()
            .Where(alert => alert.ProjectId == projectId && alert.EngineerUserId == engineerUserId && alert.Status == LocationAlertStatus.Open)
            .Select(alert => alert.RecipientUserId)
            .ToListAsync(cancellationToken);

        foreach (var recipient in recipients.Except(alreadyOpen))
        {
            db.LocationAlerts.Add(new TAlert
            {
                ProjectId = projectId,
                EngineerUserId = engineerUserId,
                RecipientUserId = recipient,
                Status = LocationAlertStatus.Open,
                StartedAt = now,
            });
        }

        return recipients.Count;
    }

    /// <summary>Closes the open alerts of one kind about an engineer. Saved by the caller.</summary>
    public async Task CloseAsync<TAlert>(int projectId, int engineerUserId, LocationAlertStatus status, DateTime now, CancellationToken cancellationToken)
        where TAlert : LocationAlert
    {
        var open = await db.LocationAlerts
            .OfType<TAlert>()
            .Where(alert => alert.ProjectId == projectId && alert.EngineerUserId == engineerUserId && alert.Status == LocationAlertStatus.Open)
            .ToListAsync(cancellationToken);

        foreach (var alert in open)
        {
            alert.Status = status;
            alert.EndedAt = now;
        }
    }

    /// <summary>Turns sharing off and ends its alerts without telling anyone. Saved by the caller.</summary>
    public async Task TurnOffAsync(LocationSharing sharing, DateTime now, CancellationToken cancellationToken)
    {
        sharing.IsOn = false;
        sharing.TurnedOffAt = now;
        sharing.UnavailableSince = null;
        sharing.OutsideSince = null;
        sharing.OutsidePings = 0;
        await CloseAsync<LocationAlert>(sharing.ProjectId, sharing.UserId, LocationAlertStatus.Ended, now, cancellationToken);
    }

    /// <summary>
    /// One round of the monitor at <paramref name="now"/>: stop sharing for engineers no longer on the project (or a
    /// completed project), then raise "location unavailable" for phones silent too long during working hours.
    /// </summary>
    public async Task CheckAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (!Options.MonitorEnabled)
        {
            return;
        }

        // Ids first, then one row at a time: a save that loses a race clears the tracker, and the next row is
        // loaded fresh.
        var leftOrClosed = await db.LocationSharings
            .Where(sharing => sharing.IsOn
                              && (sharing.Project.Status == ProjectStatus.Completed
                                  || !db.ProjectMembers.Any(member => member.ProjectId == sharing.ProjectId
                                                                      && member.UserId == sharing.UserId
                                                                      && member.EndDate == null
                                                                      && member.Responsibility == MemberResponsibility.SiteEngineer)))
            .Select(sharing => sharing.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in leftOrClosed)
        {
            if (await db.LocationSharings.FirstOrDefaultAsync(sharing => sharing.Id == id && sharing.IsOn, cancellationToken) is not { } sharing)
            {
                continue;
            }

            await TurnOffAsync(sharing, now, cancellationToken);
            if (await TrySaveAsync(cancellationToken))
            {
                log.LocationSharingEnded(sharing.UserId, sharing.ProjectId);
            }
        }

        if (!SiteLocationRules.InWorkingHours(now, clock.Zone, Options))
        {
            return;
        }

        var sharingOn = await db.LocationSharings
            .Where(sharing => sharing.IsOn && sharing.UnavailableSince == null)
            .Select(sharing => sharing.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in sharingOn)
        {
            if (await db.LocationSharings.FirstOrDefaultAsync(
                    sharing => sharing.Id == id && sharing.IsOn && sharing.UnavailableSince == null, cancellationToken) is not { } sharing)
            {
                continue;
            }

            var silentSince = SiteLocationRules.SilentSince(sharing.LastPingAt, sharing.TurnedOnAt!.Value, now, clock.Zone, Options);
            if (!SiteLocationRules.IsUnavailable(silentSince, now, Options))
            {
                continue;
            }

            sharing.UnavailableSince = silentSince;
            var told = await OpenAsync<LocationUnavailableAlert>(sharing.ProjectId, sharing.UserId, now, cancellationToken);
            if (await TrySaveAsync(cancellationToken))
            {
                log.LocationUnavailable(sharing.UserId, sharing.ProjectId, silentSince!.Value, told);
            }
        }
    }

    /// <summary>Deletes positions older than the kept days. Returns how many.</summary>
    public async Task<int> DeleteOldPositionsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-Options.KeepDays);
        var deleted = await db.LocationPings.Where(ping => ping.ReceivedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
        {
            log.LocationPingsDeleted(deleted, Options.KeepDays);
        }

        return deleted;
    }

    /// <summary>A position arriving at the same moment changed the row first: skip it this round, it is checked again next.</summary>
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        // Lost a race: the row changed under us, or the same row or alert was added at the same moment.
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
                                                   || SqlViolations.IsUnique(exception, "UX_LocationAlert_Open"))
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
