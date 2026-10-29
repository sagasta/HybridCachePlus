using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace HybridCache.Plus.Tenancy.Redis;

/// <summary>
/// Transparent multi-tenant L2 cache router implementing <see cref="IDistributedCache"/>.
/// Intercepts HybridCache L2 operations, dynamically routing them to the appropriate tenant's Redis instance
/// while acting as a non-blocking pass-through to preserve HybridCache anti-stampede semaphores.
/// </summary>
public sealed class MultiTenantDistributedCacheRouter(
    ITenantRedisConnectionPool pool,
    IOptions<MultiTenantRedisOptions> options,
    ITenantContextAccessor? tenantAccessor = null)
    : IDistributedCache
{
    private readonly ITenantRedisConnectionPool _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    private readonly MultiTenantRedisOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    // HybridCache stores tag invalidation timestamps in L2 under "__MSFT_HCT__{tag}" (no tenant prefix)
    private const string HybridCacheTagKeyPrefix = "__MSFT_HCT__";

    private IDistributedCache ResolveTargetCache(string key)
    {
        // 1. Tag invalidation records must live in one well-known place: routing them by ambient tenant would
        //    write them where readers running under another (or no) tenant never look, silently losing evictions.
        if (key.StartsWith(HybridCacheTagKeyPrefix, StringComparison.Ordinal))
        {
            return _pool.GetCacheForTenant(_options.DefaultTenantId);
        }

        // 2. A tenant-prefixed key is authoritative: the same key must always map to the same store,
        //    whatever the ambient tenant of the caller (admin jobs, impersonation, background work).
        string? tenantId = null;
        if (_options.EnableKeyPrefixTenantExtraction)
        {
            tenantId = TenantKeyParser.ExtractTenantIdString(key, _options.KeyPrefixPattern);
        }

        // 3. Ambient context resolution
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = tenantAccessor?.CurrentTenantId;
        }

        // 4. Default fallback
        if (string.IsNullOrEmpty(tenantId))
        {
            tenantId = _options.DefaultTenantId;
        }

        return _pool.GetCacheForTenant(tenantId);
    }

    /// <inheritdoc />
    public byte[]? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ResolveTargetCache(key).Get(key);
    }

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ResolveTargetCache(key).GetAsync(key, token);
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        ResolveTargetCache(key).Set(key, value, options);
    }

    /// <inheritdoc />
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        return ResolveTargetCache(key).SetAsync(key, value, options, token);
    }

    /// <inheritdoc />
    public void Refresh(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ResolveTargetCache(key).Refresh(key);
    }

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ResolveTargetCache(key).RefreshAsync(key, token);
    }

    /// <inheritdoc />
    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ResolveTargetCache(key).Remove(key);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ResolveTargetCache(key).RemoveAsync(key, token);
    }
}
