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
