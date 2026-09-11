namespace HybridCache.Plus.Backplane;

/// <summary>
/// Specifies the type of cache eviction operation to perform on recipient nodes.
/// </summary>
public enum EvictType : byte
{
    /// <summary>
    /// Purge by exact cache key.
    /// </summary>
    ByExactKey = 0,

    /// <summary>
    /// Purge all cache entries associated with a specific tag.
    /// </summary>
    ByTag = 1
}

/// <summary>
/// Compact, Native-AOT friendly payload for real-time multi-instance cache invalidation via Redis Pub/Sub.
/// </summary>
public readonly record struct BackplaneEvictionMessage(
    EvictType EvictType,
    string Target,
    string OriginInstanceId)
{
    /// <summary>
    /// Creates a key eviction message.
    /// </summary>
    public static BackplaneEvictionMessage CreateKey(string key, string originInstanceId = "") =>
        new(EvictType.ByExactKey, key, originInstanceId);

    /// <summary>
    /// Creates a tag eviction message.
    /// </summary>
    public static BackplaneEvictionMessage CreateTag(string tag, string originInstanceId = "") =>
        new(EvictType.ByTag, tag, originInstanceId);
}
