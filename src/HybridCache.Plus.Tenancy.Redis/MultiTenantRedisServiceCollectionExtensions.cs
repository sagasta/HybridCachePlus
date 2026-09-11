using HybridCache.Plus;
using HybridCache.Plus.Tenancy;
using HybridCache.Plus.Tenancy.Redis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extension methods for registering Multi-Tenant Redis L2 Cache with HybridCache.Plus.
/// </summary>
public static class MultiTenantRedisServiceCollectionExtensions
{
    /// <summary>
    /// Enables dynamic multi-tenant L2 cache routing across multiple Redis clusters or endpoints.
    /// </summary>
    public static HybridCachePlusBuilder UseMultiTenantRedisL2(
        this HybridCachePlusBuilder builder,
        Action<MultiTenantRedisOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<MultiTenantRedisOptions>();
        if (configure != null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.TryAddSingleton<ITenantContextAccessor, AsyncLocalTenantContextAccessor>();
        builder.Services.AddSingleton<ITenantRedisConnectionPool, TenantRedisConnectionPool>();
        builder.Services.AddSingleton<IDistributedCache, MultiTenantDistributedCacheRouter>();

        return builder;
    }
}
