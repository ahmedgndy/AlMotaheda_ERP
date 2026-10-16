using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>
/// The cache key and tag conventions, in one place (docs/modernization/17-caching-changes.md).
/// Keys: <c>{module}:{entity}:{scope}:v{n}</c>. Tags: <c>agg:{Aggregate}</c> and <c>agg:{Aggregate}:{projectId}</c>,
/// named like the aggregates of the live-update dependency map, so a change removes exactly what depends on it.
/// </summary>
public static class CacheTags
{
    /// <summary>Every cached copy of an aggregate, on every project.</summary>
    public static string Aggregate(string aggregate) => $"agg:{aggregate}";

    /// <summary>The cached copies of an aggregate for one project.</summary>
    public static string Aggregate(string aggregate, int projectId) => $"agg:{aggregate}:{projectId}";

    /// <summary>Both tags of a project aggregate: removed by a change on that project, or by one whose project isn't known.</summary>
    public static string[] ForProject(string aggregate, int projectId) => [Aggregate(aggregate, projectId), Aggregate(aggregate)];
}

/// <summary>Told about the records each save changed, right after it (cache removal). Must not throw.</summary>
public interface IChangeObserver
{
    Task OnChangedAsync(IReadOnlyList<EntityChange> changes, CancellationToken cancellationToken);
}

public static class CacheRegistration
{
    /// <summary>
    /// <see cref="HybridCache"/> for every module: in memory today (one server); a Redis second level can be added in
    /// the host later without code changes. Short lifetimes are only a backstop: entries are removed by tag when the
    /// records they depend on change.
    /// </summary>
    public static IServiceCollection AddErpCache(this IServiceCollection services)
    {
        services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 8 * 1024 * 1024;
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(5),
                LocalCacheExpiration = TimeSpan.FromMinutes(5),
            };
        });
        return services;
    }
}
