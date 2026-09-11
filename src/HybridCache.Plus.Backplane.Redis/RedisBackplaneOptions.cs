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
}
