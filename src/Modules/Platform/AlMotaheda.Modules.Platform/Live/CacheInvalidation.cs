using AlMotaheda.BuildingBlocks.Data;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Platform.Live;

/// <summary>
/// Removes the cached copies of what a save changed, by tag, from the same dependency map as the live signals
/// (<see cref="LiveScopes.CacheTagsFor"/>). Runs right after every save and again after the outbox commit.
/// A failure is logged and swallowed: the entries then expire on their own (short lifetimes).
/// </summary>
internal sealed class CacheInvalidation(HybridCache cache, ILogger<CacheInvalidation> logger) : IChangeObserver
{
    public async Task OnChangedAsync(IReadOnlyList<EntityChange> changes, CancellationToken cancellationToken)
    {
        var tags = changes.SelectMany(LiveScopes.CacheTagsFor).Distinct().ToList();
        if (tags.Count == 0)
        {
            return;
        }

        try
        {
            await cache.RemoveByTagAsync(tags, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Cache entries tagged {Tags} could not be removed; they expire on their own", string.Join(", ", tags));
        }
    }
}
