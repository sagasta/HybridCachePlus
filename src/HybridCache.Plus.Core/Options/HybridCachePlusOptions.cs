using Microsoft.Extensions.Caching.Hybrid;

namespace HybridCache.Plus.Options;

/// <summary>
/// Configuration and options for HybridCache.Plus operations.
/// </summary>
public static class HybridCachePlusOptions
{
    /// <summary>
    /// Creates a configured <see cref="HybridCacheEntryOptions"/> with specified local and distributed expiration.
    /// </summary>
    public static HybridCacheEntryOptions CreateEntryOptions(
        int localTtlSeconds, 
        int distributedTtlSeconds, 
        HybridCacheEntryFlags flags = HybridCacheEntryFlags.None)
    {
        return new HybridCacheEntryOptions
        {
            Flags = flags,
            LocalCacheExpiration = localTtlSeconds > 0 ? TimeSpan.FromSeconds(localTtlSeconds) : null,
            Expiration = distributedTtlSeconds > 0 ? TimeSpan.FromSeconds(distributedTtlSeconds) : null
        };
    }
}
