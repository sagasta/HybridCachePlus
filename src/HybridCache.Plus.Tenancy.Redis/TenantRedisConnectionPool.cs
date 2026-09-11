using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HybridCache.Plus.Tenancy.Redis;

/// <summary>
/// Thread-safe connection pool managing lazy-initialized, isolated <see cref="IDistributedCache"/> instances per tenant.
/// </summary>
public interface ITenantRedisConnectionPool
{
    /// <summary>
    /// Gets or creates the <see cref="IDistributedCache"/> instance associated with the specified tenant.
    /// </summary>
    IDistributedCache GetCacheForTenant(string tenantId);
}

/// <summary>
/// Default implementation of <see cref="ITenantRedisConnectionPool"/>.
/// </summary>
public sealed class TenantRedisConnectionPool(
    IOptions<MultiTenantRedisOptions> options,
    IServiceProvider serviceProvider)
    : ITenantRedisConnectionPool, IAsyncDisposable, IDisposable
{
    private readonly MultiTenantRedisOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly IServiceProvider _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    private readonly ConcurrentDictionary<string, IDistributedCache> _caches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IConnectionMultiplexer> _ownedMultiplexers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <inheritdoc />
    public IDistributedCache GetCacheForTenant(string tenantId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalizedTenantId = string.IsNullOrWhiteSpace(tenantId)
            ? _options.DefaultTenantId
            : tenantId;

        return _caches.GetOrAdd(normalizedTenantId, CreateCacheForTenant);
    }

    private IDistributedCache CreateCacheForTenant(string tenantId)
    {
        // 1. Check custom DistributedCacheResolver
        if (_options.DistributedCacheResolver != null)
        {
            var customCache = _options.DistributedCacheResolver(tenantId, _serviceProvider);
            if (customCache != null)
            {
                return customCache;
            }
        }

        // 2. Check MultiplexerResolver
        if (_options.MultiplexerResolver != null)
        {
            var mux = _options.MultiplexerResolver(tenantId);
            if (mux != null)
            {
                return CreateRedisCacheFromMultiplexer(mux, tenantId);
            }
        }

        // 3. Check ConnectionStringResolver
        if (_options.ConnectionStringResolver != null)
        {
            var connStr = _options.ConnectionStringResolver(tenantId);
            if (!string.IsNullOrEmpty(connStr))
            {
                var mux = ConnectionMultiplexer.Connect(connStr!);
                _ownedMultiplexers[tenantId] = mux;
                return CreateRedisCacheFromMultiplexer(mux, tenantId);
            }
        }

        // 4. Fallback to default tenant if not already on it
        if (!string.Equals(tenantId, _options.DefaultTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return GetCacheForTenant(_options.DefaultTenantId);
        }

        // 5. Fallback in-memory distributed cache
        return new MemoryDistributedCache(new OptionsWrapper<MemoryDistributedCacheOptions>(new MemoryDistributedCacheOptions()));
    }

    private static IDistributedCache CreateRedisCacheFromMultiplexer(IConnectionMultiplexer multiplexer, string instancePrefix)
    {
        var redisOptions = new RedisCacheOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer),
            InstanceName = $"{instancePrefix}:"
        };
        return new RedisCache(redisOptions);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var mux in _ownedMultiplexers.Values)
        {
            mux.Dispose();
        }
        _ownedMultiplexers.Clear();
        _caches.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var mux in _ownedMultiplexers.Values)
        {
            await mux.DisposeAsync().ConfigureAwait(false);
        }
        _ownedMultiplexers.Clear();
        _caches.Clear();
    }
}
