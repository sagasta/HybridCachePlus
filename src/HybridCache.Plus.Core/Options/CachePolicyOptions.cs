using Microsoft.Extensions.Caching.Hybrid;

namespace HybridCache.Plus.Options;

/// <summary>
/// Cache entry options for a specific policy.
/// </summary>
public sealed class CachePolicyOptions
{
    /// <summary>
    /// Local (L1) expiration time in seconds.
    /// </summary>
    public int? LocalTtlSeconds { get; set; }

    /// <summary>
    /// Distributed (L2) expiration time in seconds.
    /// </summary>
    public int? DistributedTtlSeconds { get; set; }

    /// <summary>
    /// Cache entry flags (e.g. None, DisableLocalCache, DisableDistributedCache).
    /// </summary>
    public HybridCacheEntryFlags? Flags { get; set; }

    /// <summary>
    /// Fail-safe backup lifetime in seconds (0 disables fail-safe). Only applies to methods that opt in to fail-safe
    /// through <c>[CacheTemplate(FailSafeMaxAgeSeconds = ...)]</c>.
    /// </summary>
    public int? FailSafeMaxAgeSeconds { get; set; }

    /// <summary>
    /// Fail-safe soft timeout for the factory in milliseconds (0 disables it).
    /// </summary>
    public int? FactorySoftTimeoutMs { get; set; }

    /// <summary>
    /// Seconds a fail-safe value is served locally after a factory failure before retrying (0 uses the 30 second default).
    /// </summary>
    public int? FailSafeThrottleSeconds { get; set; }

    /// <summary>
    /// Creates a <see cref="HybridCacheEntryOptions"/> instance representing this policy, using fallback defaults when properties are null.
    /// </summary>
    public HybridCacheEntryOptions ToEntryOptions(HybridCacheEntryOptions fallback)
    {
        return new HybridCacheEntryOptions
        {
            Flags = Flags ?? fallback.Flags,
            LocalCacheExpiration = LocalTtlSeconds.HasValue
                ? (LocalTtlSeconds.Value > 0 ? TimeSpan.FromSeconds(LocalTtlSeconds.Value) : null)
                : fallback.LocalCacheExpiration,
            Expiration = DistributedTtlSeconds.HasValue
                ? (DistributedTtlSeconds.Value > 0 ? TimeSpan.FromSeconds(DistributedTtlSeconds.Value) : null)
                : fallback.Expiration
        };
    }
}
