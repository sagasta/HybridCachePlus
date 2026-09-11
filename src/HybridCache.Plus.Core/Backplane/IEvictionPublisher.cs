namespace HybridCache.Plus.Backplane;

/// <summary>
/// Defines a publisher contract for broadcasting cache eviction events across instances.
/// </summary>
public interface IEvictionPublisher
{
    /// <summary>
    /// Publishes a cache invalidation message to the distributed backplane.
    /// </summary>
    ValueTask PublishAsync(BackplaneEvictionMessage message, CancellationToken cancellationToken = default);
}
