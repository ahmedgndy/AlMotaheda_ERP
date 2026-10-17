using AlMotaheda.BuildingBlocks.Data;
using System.Text.Json;
using AlMotaheda.Modules.Platform.Common;
using AlMotaheda.Modules.Platform.Data;
using AlMotaheda.Modules.Platform.Live;
using AlMotaheda.Modules.Platform.Notifications;
using AlMotaheda.Modules.Platform.Push;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Platform.Outbox;

/// <summary>
/// Handles the outbox after the changes are committed: it stores the notifications each change causes
/// (<see cref="NotificationRules"/>) and, once that is committed too, sends the live signals: each notification to
/// its recipient and each change to the groups of <see cref="LiveScopes"/>. Cache invalidation will hang off the same loop.
/// </summary>
/// <remarks>
/// <para>Each batch runs in one transaction: the messages are locked (<c>UPDLOCK, READPAST</c>, so a second server
/// would take other messages), their notifications are inserted, and they are marked handled, all together. So a
/// message is handled once, or not at all and then again; the unique source index stops duplicates either way.</para>
/// <para>A message that fails is retried on later rounds and skipped after <see cref="OutboxOptions.MaxAttempts"/>.
/// When the database is unreachable the loop waits longer each time (up to a minute) and logs once per outage.</para>
/// <para>Live signals are best effort: if sending fails the data is still right, and clients refetch on reconnect.</para>
/// </remarks>
internal sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    TimeProvider time,
    PlatformLog log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var handled = await DispatchBatchAsync(stoppingToken);
                if (failures > 0)
                {
                    log.DispatchRecovered(failures);
                    failures = 0;
                }

                // A full batch means more may be waiting: go again at once.
                delay = handled >= settings.BatchSize ? TimeSpan.Zero : settings.PollInterval;
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                failures++;
                delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, settings.PollInterval.Ticks * (1L << Math.Min(failures, 10))));
                if (failures == 1)
                {
                    log.DispatchFailed(exception, delay.TotalSeconds);
                }
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Handles one batch of messages. Returns how many were taken.</summary>
    internal async Task<int> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var messages = await db.OutboxMessages
            .FromSqlRaw(
                $"""
                SELECT TOP (@batchSize) * FROM [{PlatformTables.Schema}].[{PlatformTables.OutboxMessage}] WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE [ProcessedAt] IS NULL AND [Attempts] < @maxAttempts
                ORDER BY [Id]
                """,
                new SqlParameter("@batchSize", settings.BatchSize),
                new SqlParameter("@maxAttempts", settings.MaxAttempts))
            .ToListAsync(cancellationToken);

        var live = new List<LiveMessage>();
        var stored = new List<Notification>();
        var changed = new List<EntityChange>();
        foreach (var message in messages)
        {
            try
            {
                var notifications = Handle(message, time.GetUtcNow().UtcDateTime).ToList();
                notifications.AddRange(await ReviewNotificationsAsync(scope.ServiceProvider, message, notifications, time.GetUtcNow().UtcDateTime, cancellationToken));
                var changes = Changes(message).ToList();
                changed.AddRange(ChangedRecords(message));
                db.Notifications.AddRange(notifications);
                stored.AddRange(notifications);
                live.AddRange(changes);
                message.ProcessedAt = time.GetUtcNow().UtcDateTime;
                message.LastError = null;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
            {
                message.Attempts++;
                message.LastError = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
                log.MessageFailed(exception, message.Id, message.Attempts);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Only after the commit: a signal never announces something that could still roll back.
        live.AddRange(stored.Select(notification => new LiveMessage(
            LiveScopes.User(notification.RecipientUserId),
            LiveMethods.Notification,
            new NotificationSignal(notification.Id, notification.Type, notification.EntityType, notification.EntityKey, notification.ProjectId))));
        // The cache was already cleared right after each save; clearing again after the commit closes the gap
        // between a save and its handler's commit.
        foreach (var observer in scope.ServiceProvider.GetServices<IChangeObserver>())
        {
            await observer.OnChangedAsync(changed, cancellationToken);
        }

        await PublishAsync(scope.ServiceProvider, live, cancellationToken);

        // Push to the recipients' phones, also only after the commit and also best effort (docs: Push/PushDelivery).
        if (scope.ServiceProvider.GetService<PushDelivery>() is { } push)
        {
            try
            {
                await push.SendAsync(db, stored, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                log.LiveUpdatesFailed(exception, stored.Count);
            }
        }

        return messages.Count;
    }

    /// <summary>
    /// "Waits for your review" notifications (docs/cost-flow §2.8): their recipients are read from Access and
    /// Projects, so they are found here, in the batch's transaction, rather than by the pure rules.
    /// </summary>
    private static async Task<IReadOnlyList<Notification>> ReviewNotificationsAsync(
        IServiceProvider services, OutboxMessage message, IReadOnlyList<Notification> already, DateTime now, CancellationToken cancellationToken)
    {
        if (services.GetService<ReviewRecipients>() is not { } recipients)
        {
            return [];
        }

        var notifications = new List<Notification>();
        foreach (var change in ChangedRecords(message))
        {
            foreach (var draft in await recipients.DraftsAsync(change, message.UserId, cancellationToken))
            {
                if (already.Any(existing => existing.RecipientUserId == draft.RecipientUserId && existing.EntityType == change.Entity
                        && existing.EntityKey == change.Key && existing.Type == draft.Type)
                    || notifications.Any(existing => existing.RecipientUserId == draft.RecipientUserId && existing.EntityType == change.Entity
                        && existing.EntityKey == change.Key))
                {
                    continue;
                }

                notifications.Add(new Notification
                {
                    RecipientUserId = draft.RecipientUserId,
                    Type = draft.Type,
                    TitleAr = draft.TitleAr,
                    TitleEn = draft.TitleEn,
                    EntityType = change.Entity,
                    EntityKey = change.Key,
                    ProjectId = change.ProjectId,
                    SourceMessageId = message.MessageId,
                    CreatedAt = now,
                });
            }
        }

        return notifications;
    }

    private static IEnumerable<EntityChange> ChangedRecords(OutboxMessage message) =>
        message.Type != PlatformTables.EntitiesChangedMessage
            ? []
            : JsonSerializer.Deserialize<EntitiesChangedPayload>(message.Payload, Json)?.Changes ?? [];

    /// <summary>The change signals of one message (no amounts). Unknown message types send none.</summary>
    internal static IEnumerable<LiveMessage> Changes(OutboxMessage message)
    {
        if (message.Type != PlatformTables.EntitiesChangedMessage)
        {
            return [];
        }

        var payload = JsonSerializer.Deserialize<EntitiesChangedPayload>(message.Payload, Json)
                      ?? throw new JsonException("The outbox payload is empty.");
        return payload.Changes.SelectMany(change => LiveScopes.For(change, message.Id)).ToList();
    }

    private async Task PublishAsync(IServiceProvider services, IReadOnlyList<LiveMessage> live, CancellationToken cancellationToken)
    {
        if (live.Count == 0 || services.GetService<ILiveUpdates>() is not { } updates)
        {
            return;
        }

        try
        {
            await updates.PublishAsync(live, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.LiveUpdatesFailed(exception, live.Count);
        }
    }

    /// <summary>The notifications one message causes. Unknown message types cause none.</summary>
    internal static IEnumerable<Notification> Handle(OutboxMessage message, DateTime now)
    {
        if (message.Type != PlatformTables.EntitiesChangedMessage)
        {
            return [];
        }

        var payload = JsonSerializer.Deserialize<EntitiesChangedPayload>(message.Payload, Json)
                      ?? throw new JsonException("The outbox payload is empty.");

        return payload.Changes
            .SelectMany(change => NotificationRules.For(change, message.UserId).Select(draft => new Notification
            {
                RecipientUserId = draft.RecipientUserId,
                Type = draft.Type,
                TitleAr = draft.TitleAr,
                TitleEn = draft.TitleEn,
                EntityType = change.Entity,
                EntityKey = change.Key,
                ProjectId = change.ProjectId,
                SourceMessageId = message.MessageId,
                CreatedAt = now,
            }))
            // The same save can change one record twice (rare); one notification per record and type is enough.
            .DistinctBy(notification => (notification.RecipientUserId, notification.EntityType, notification.EntityKey, notification.Type))
            .ToList();
    }
}
