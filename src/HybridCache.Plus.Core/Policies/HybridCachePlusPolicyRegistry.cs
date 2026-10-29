using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Hybrid;
using HybridCache.Plus.Options;
using HybridCache.Plus.Tenancy;

namespace HybridCache.Plus.Policies;

/// <summary>
/// Pre-computed registry resolving cache entry options with cascading multi-tenant overrides and zero-allocation execution.
/// </summary>
public sealed class HybridCachePlusPolicyRegistry
{
    private static volatile HybridCachePlusPolicyRegistry _current = new(new HybridCachePlusPolicyOptions());
    private static volatile ITenantContextAccessor? _ambientTenantAccessor;
    private static readonly ConditionalWeakTable<object, HybridCachePlusPolicyRegistry> _cacheRegistries = new();

    private readonly ITenantContextAccessor? _tenantAccessor;

    // Options and their memoized resolutions are swapped together, so a reload can never mix old and new results.
    private volatile State _state;

    /// <summary>
    /// Creates a registry for the specified policy options.
    /// </summary>
    /// <param name="options">The policy options.</param>
    /// <param name="tenantAccessor">
    /// Ambient tenant accessor used when no explicit tenant is supplied.
    /// When null, the accessor set through <see cref="SetAmbientTenantAccessor"/> is used.
    /// </param>
    public HybridCachePlusPolicyRegistry(HybridCachePlusPolicyOptions options, ITenantContextAccessor? tenantAccessor = null)
    {
        _state = new State(options ?? throw new ArgumentNullException(nameof(options)));
        _tenantAccessor = tenantAccessor;
    }

    /// <summary>
    /// Gets or sets the globally active policy registry instance, used for caches without an associated registry.
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
    /// Associates a specific <see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/> instance with a registry,
    /// so several hosts in the same process (e.g. integration tests) each resolve their own policies.
    /// </summary>
    public static void Register(object cache, HybridCachePlusPolicyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(registry);
        _cacheRegistries.AddOrUpdate(cache, registry);
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
    /// Resolves the effective <see cref="HybridCacheEntryOptions"/> using the registry associated with <paramref name="cache"/>,
    /// or <see cref="Current"/> when the cache has none.
    /// </summary>
    public static HybridCacheEntryOptions Resolve(
        object? cache,
        string policyName,
        string? tenantId,
        HybridCacheEntryOptions fallback)
    {
        var registry = cache != null && _cacheRegistries.TryGetValue(cache, out var associated)
            ? associated
            : _current;

        return registry.ResolveOptions(policyName, tenantId, fallback);
    }

    /// <summary>
    /// Replaces the policy options (e.g. after an <c>appsettings.json</c> change) and discards previously resolved entries.
    /// </summary>
    public void Reload(HybridCachePlusPolicyOptions options)
    {
        _state = new State(options ?? throw new ArgumentNullException(nameof(options)));
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

        var state = _state;

        var effectiveTenantId = !string.IsNullOrEmpty(tenantId)
            ? tenantId
            : (_tenantAccessor ?? _ambientTenantAccessor)?.CurrentTenantId;

        // Only tenants with configured overrides get their own entry; every other tenant shares the global
        // resolution, so the memoization stays bounded no matter how many tenant identifiers are seen.
        if (string.IsNullOrEmpty(effectiveTenantId) || !state.Options.Tenants.ContainsKey(effectiveTenantId))
        {
            effectiveTenantId = null;
        }

        // The fallback (compiled attribute TTLs) is part of the key: several methods may share a policy name
        // while declaring different defaults, and each must keep its own.
        var key = new ResolutionKey(policyName, effectiveTenantId, fallback.LocalCacheExpiration, fallback.Expiration, fallback.Flags);

        return state.Resolved.TryGetValue(key, out var resolved)
            ? resolved
            : state.Resolved.GetOrAdd(key, ComputeOptions(state.Options, policyName, effectiveTenantId, fallback));
    }

    private sealed class State(HybridCachePlusPolicyOptions options)
    {
        public HybridCachePlusPolicyOptions Options { get; } = options;
        public ConcurrentDictionary<ResolutionKey, HybridCacheEntryOptions> Resolved { get; } = new();
    }

    private readonly record struct ResolutionKey(
        string PolicyName,
        string? TenantId,
        TimeSpan? LocalCacheExpiration,
        TimeSpan? Expiration,
        HybridCacheEntryFlags? Flags);

    private static HybridCacheEntryOptions ComputeOptions(
        HybridCachePlusPolicyOptions options,
        string policyName,
        string? tenantId,
        HybridCacheEntryOptions fallback)
    {
        int? localTtl = null;
        int? distributedTtl = null;
        HybridCacheEntryFlags? flags = null;

        // 1. Check Tenant-specific policy override
        if (!string.IsNullOrEmpty(tenantId) &&
            options.Tenants.TryGetValue(tenantId, out var tenantOptions))
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
        if (options.Policies.TryGetValue(policyName, out var globalPolicy))
        {
            localTtl ??= globalPolicy.LocalTtlSeconds;
            distributedTtl ??= globalPolicy.DistributedTtlSeconds;
            flags ??= globalPolicy.Flags;
        }

        // 3. Check Global defaults
        localTtl ??= options.DefaultLocalTtlSeconds;
        distributedTtl ??= options.DefaultDistributedTtlSeconds;

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
