using StackExchange.Redis;

namespace HybridCache.Plus.Backplane.Redis;

/// <summary>
/// Configuration options for the Redis Pub/Sub multi-instance eviction backplane.
/// </summary>
public sealed class RedisBackplaneOptions
{
    /// <summary>
    /// The Redis Pub/Sub channel name for broadcasting eviction messages.
    /// Default is "hybridcache:plus:evictions".
    /// </summary>
    public string ChannelName { get; set; } = "hybridcache:plus:evictions";

    /// <summary>
    /// The unique identifier of this running process/pod instance.
    /// Used to avoid re-processing own eviction echoes.
    /// Default is a new unique GUID string.
    /// </summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Explicit <see cref="IConnectionMultiplexer"/> instance to use for Pub/Sub.
    /// If null, the backplane will try to resolve <see cref="IConnectionMultiplexer"/> from DI or <see cref="ConnectionMultiplexerFactory"/>.
    /// </summary>
    public IConnectionMultiplexer? ConnectionMultiplexer { get; set; }

    /// <summary>
    /// Optional factory delegate to resolve <see cref="IConnectionMultiplexer"/> from the service provider.
    /// </summary>
    public Func<IServiceProvider, IConnectionMultiplexer>? ConnectionMultiplexerFactory { get; set; }

    /// <summary>
    /// Optional Redis connection string used to create a connection if no multiplexer is provided.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// When true (default), a remote key eviction only drops the entry from this instance's local L1 cache.
    /// The origin instance has already removed it from L2, so deleting it again from every instance only adds
    /// redundant Redis round-trips (and can delete a value another instance has just re-populated).
    /// Applies to the default HybridCache implementation; other implementations always fall back to <c>RemoveAsync</c>.
    /// Tag evictions always go through <c>RemoveByTagAsync</c>, as HybridCache exposes no L1-only tag invalidation.
    /// </summary>
    public bool EvictLocalCacheOnly { get; set; } = true;
}
