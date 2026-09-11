using System.Runtime.CompilerServices;

namespace HybridCache.Plus.Backplane;

/// <summary>
/// Ambient and per-cache context holding the active <see cref="IEvictionPublisher"/>.
/// Enables compiled HybridCache extension methods to publish eviction events automatically across instances.
/// </summary>
public static class HybridCachePlusBackplaneContext
{
    private static volatile IEvictionPublisher? _currentPublisher;
    private static readonly ConditionalWeakTable<object, IEvictionPublisher> _cachePublishers = new();

    /// <summary>
    /// Gets or sets the globally active fallback eviction publisher.
    /// </summary>
    public static IEvictionPublisher? CurrentPublisher
    {
        get => _currentPublisher;
        set => _currentPublisher = value;
    }

    /// <summary>
    /// Associates a specific <see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/> instance with an <see cref="IEvictionPublisher"/>.
    /// Enables multi-instance concurrency within the same process (e.g. testing environments).
    /// </summary>
    public static void SetPublisherForCache(object cache, IEvictionPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(publisher);
        _cachePublishers.AddOrUpdate(cache, publisher);
        _currentPublisher = publisher;
    }

    /// <summary>
    /// Retrieves the eviction publisher associated with the specified cache, or the ambient publisher.
    /// </summary>
    public static IEvictionPublisher? GetPublisher(object? cache)
    {
        if (cache != null && _cachePublishers.TryGetValue(cache, out var publisher))
        {
            return publisher;
        }
        return _currentPublisher;
    }
}
