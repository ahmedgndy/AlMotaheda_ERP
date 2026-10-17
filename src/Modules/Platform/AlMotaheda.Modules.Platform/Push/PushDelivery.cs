using AlMotaheda.Modules.Platform.Common;
using AlMotaheda.Modules.Platform.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Platform.Push;

/// <summary>
/// Sends the push for notifications that are already stored and committed, like the live signals: best effort. A failed
/// push loses nothing, because the notification is in the user's list and the app reads it at the next refresh.
/// </summary>
internal sealed class PushDelivery(IPushSender sender, TimeProvider time, PlatformLog log)
{
    public async Task SendAsync(PlatformDbContext db, IReadOnlyList<Notification> notifications, CancellationToken cancellationToken)
    {
        if (notifications.Count == 0 || !sender.IsConfigured)
        {
            return;
        }

        var recipients = notifications.Select(notification => notification.RecipientUserId).Distinct().ToList();
        var devices = await db.DeviceTokens
            .Where(device => recipients.Contains(device.UserId) && device.DisabledAt == null)
            .Select(device => new { device.Id, device.UserId, device.Token })
            .ToListAsync(cancellationToken);
        if (devices.Count == 0)
        {
            return;
        }

        var gone = new List<long>();
        foreach (var notification in notifications)
        {
            var message = new PushMessage(notification.Id, notification.Type, notification.TitleAr, notification.TitleEn,
                notification.EntityType, notification.EntityKey, notification.ProjectId);
            var sent = 0;
            foreach (var device in devices.Where(device => device.UserId == notification.RecipientUserId && !gone.Contains(device.Id)))
            {
                PushResult result;
                try
                {
                    result = await sender.SendAsync(device.Token, message, cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    result = PushResult.Failed;
                }

                switch (result)
                {
                    case PushResult.Sent:
                        sent++;
                        break;
                    case PushResult.TokenGone:
                        gone.Add(device.Id);
                        log.DeviceDisabled(device.Id);
                        break;
                    default:
                        log.PushFailed(notification.Id, device.Id, result.ToString());
                        break;
                }
            }

            if (sent > 0)
            {
                log.PushSent(notification.Id, sent);
            }
        }

        if (gone.Count > 0)
        {
            var now = time.GetUtcNow().UtcDateTime;
            await db.DeviceTokens.Where(device => gone.Contains(device.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(device => device.DisabledAt, now), cancellationToken);
        }
    }
}
