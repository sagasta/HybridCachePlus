using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace HybridCache.Plus.Tenancy.Redis;

/// <summary>
/// Configuration options for the Multi-Tenant L2 Redis router.
/// </summary>
public sealed class MultiTenantRedisOptions
{
    /// <summary>
    /// Fallback tenant identifier when no tenant can be inferred from context or key.
    /// Default is "default".
    /// </summary>
    public string DefaultTenantId { get; set; } = "default";

    /// <summary>
    /// The prefix pattern used to identify tenant sections in formatted keys.
    /// Default is "tenants:".
    /// </summary>
    public string KeyPrefixPattern { get; set; } = "tenants:";

    /// <summary>
    /// Gets or sets whether to extract tenant IDs from key prefixes when ambient context is not set.
    /// Default is true.
    /// </summary>
    public bool EnableKeyPrefixTenantExtraction { get; set; } = true;

    /// <summary>
    /// Delegate to resolve a Redis connection string for a given tenant.
    /// </summary>
    public Func<string, string?>? ConnectionStringResolver { get; private set; }

    /// <summary>
    /// Delegate to resolve a pre-existing <see cref="IConnectionMultiplexer"/> for a given tenant.
    /// </summary>
    public Func<string, IConnectionMultiplexer?>? MultiplexerResolver { get; private set; }

    /// <summary>
    /// Delegate to resolve a custom <see cref="IDistributedCache"/> instance for a given tenant.
    /// </summary>
    public Func<string, IServiceProvider, IDistributedCache?>? DistributedCacheResolver { get; private set; }

    /// <summary>
    /// Configures a connection string resolver delegate for dynamically routing tenants to Redis endpoints.
    /// </summary>
    public MultiTenantRedisOptions ResolveConnectionString(Func<string, string?> resolver)
    {
        ConnectionStringResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        return this;
    }

    /// <summary>
    /// Configures a multiplexer resolver delegate for routing tenants to specific <see cref="IConnectionMultiplexer"/> instances.
    /// </summary>
    public MultiTenantRedisOptions ResolveMultiplexer(Func<string, IConnectionMultiplexer?> resolver)
    {
        MultiplexerResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        return this;
    }

    /// <summary>
    /// Configures a custom distributed cache resolver delegate for routing tenants to specific <see cref="IDistributedCache"/> backends.
    /// </summary>
    public MultiTenantRedisOptions ResolveDistributedCache(Func<string, IServiceProvider, IDistributedCache?> resolver)
    {
        DistributedCacheResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        return this;
    }
}
