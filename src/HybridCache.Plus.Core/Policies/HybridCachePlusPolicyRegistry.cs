using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Hybrid;
using HybridCache.Plus.Options;
using HybridCache.Plus.Tenancy;

namespace HybridCache.Plus.Policies;

/// <summary>
/// Pre-computed registry resolving cache entry options with cascading multi-tenant overrides and zero-allocation execution.
/// </summary>
public sealed class HybridCachePlusPolicyRegistry(HybridCachePlusPolicyOptions options)
{
    private static volatile HybridCachePlusPolicyRegistry _current = new(new HybridCachePlusPolicyOptions());
    private static volatile ITenantContextAccessor? _ambientTenantAccessor;

    private readonly HybridCachePlusPolicyOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ConcurrentDictionary<string, HybridCacheEntryOptions> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the globally active policy registry instance.
    /// </summary>
    public static HybridCachePlusPolicyRegistry Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Sets the ambient tenant context accessor for fallback tenant extraction when no explicit tenant parameter is provided.
    /// </summary>
    public static void SetAmbientTenantAccessor(ITenantContextAccessor? accessor)
    {
        _ambientTenantAccessor = accessor;
    }

    /// <summary>
    /// Resolves the effective <see cref="HybridCacheEntryOptions"/> for the specified policy and tenant, applying cascading fallback.
    /// </summary>
    public static HybridCacheEntryOptions Resolve(
        string policyName,
        string? tenantId,
        HybridCacheEntryOptions fallback)
    {
        return _current.ResolveOptions(policyName, tenantId, fallback);
    }

    /// <summary>
    /// Resolves the effective <see cref="HybridCacheEntryOptions"/> for the specified policy and tenant from this registry instance.
    /// </summary>
    public HybridCacheEntryOptions ResolveOptions(
        string policyName,
        string? tenantId,
        HybridCacheEntryOptions fallback)
    {
        ArgumentNullException.ThrowIfNull(policyName);
        ArgumentNullException.ThrowIfNull(fallback);

        var effectiveTenantId = !string.IsNullOrEmpty(tenantId)
            ? tenantId
            : _ambientTenantAccessor?.CurrentTenantId;

        var cacheKey = !string.IsNullOrEmpty(effectiveTenantId)
            ? $"tenant:{effectiveTenantId}:{policyName}"
            : $"global:{policyName}";

        return _cache.GetOrAdd(cacheKey, _ => ComputeOptions(policyName, effectiveTenantId, fallback));
    }

    private HybridCacheEntryOptions ComputeOptions(
        string policyName,
        string? tenantId,
        HybridCacheEntryOptions fallback)
    {
        int? localTtl = null;
        int? distributedTtl = null;
        HybridCacheEntryFlags? flags = null;

        // 1. Check Tenant-specific policy override
        if (!string.IsNullOrEmpty(tenantId) &&
            _options.Tenants.TryGetValue(tenantId, out var tenantOptions))
        {
            if (tenantOptions.Policies.TryGetValue(policyName, out var tenantPolicy))
            {
                localTtl = tenantPolicy.LocalTtlSeconds;
                distributedTtl = tenantPolicy.DistributedTtlSeconds;
                flags = tenantPolicy.Flags;
            }

            // 1b. Tenant default TTL fallback
            localTtl ??= tenantOptions.DefaultLocalTtlSeconds;
            distributedTtl ??= tenantOptions.DefaultDistributedTtlSeconds;
        }

        // 2. Check Global policy override
        if (_options.Policies.TryGetValue(policyName, out var globalPolicy))
        {
            localTtl ??= globalPolicy.LocalTtlSeconds;
            distributedTtl ??= globalPolicy.DistributedTtlSeconds;
            flags ??= globalPolicy.Flags;
        }

        // 3. Check Global defaults
        localTtl ??= _options.DefaultLocalTtlSeconds;
        distributedTtl ??= _options.DefaultDistributedTtlSeconds;

        // If no overrides were matched at any level, return the static compiled fallback directly
        if (!localTtl.HasValue && !distributedTtl.HasValue && !flags.HasValue)
        {
            return fallback;
        }

        return new HybridCacheEntryOptions
        {
            Flags = flags ?? fallback.Flags,
            LocalCacheExpiration = localTtl.HasValue
                ? (localTtl.Value > 0 ? TimeSpan.FromSeconds(localTtl.Value) : null)
                : fallback.LocalCacheExpiration,
            Expiration = distributedTtl.HasValue
                ? (distributedTtl.Value > 0 ? TimeSpan.FromSeconds(distributedTtl.Value) : null)
                : fallback.Expiration
        };
    }
}
