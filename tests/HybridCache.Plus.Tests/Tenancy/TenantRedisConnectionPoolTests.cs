using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using HybridCache.Plus.Tenancy.Redis;

namespace HybridCache.Plus.Tests.Tenancy;

public class TenantRedisConnectionPoolTests
{
    [Fact]
    public async Task GetCacheForTenant_ConcurrentFirstAccess_CreatesTheTenantCacheExactlyOnce()
    {
        var creations = 0;
        var options = new MultiTenantRedisOptions();
        options.ResolveDistributedCache((_, _) =>
        {
            Interlocked.Increment(ref creations);
            Thread.Sleep(50); // widen the race window, like a real connection handshake
            return new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        });

        using var pool = new TenantRedisConnectionPool(
            Microsoft.Extensions.Options.Options.Create(options),
            new ServiceCollection().BuildServiceProvider());

        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return pool.GetCacheForTenant("tenant_a");
            }))
            .ToArray();

        start.Set();
        var caches = await Task.WhenAll(tasks);

        Assert.Equal(1, creations);
        Assert.All(caches, cache => Assert.Same(caches[0], cache));
    }

    [Fact]
    public void GetCacheForTenant_UnknownTenantsBeyondLimit_AreNotRetained()
    {
        var resolutions = new ConcurrentDictionary<string, int>();
        var defaultCache = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var options = new MultiTenantRedisOptions { MaxFallbackTenantEntries = 2 };
        options.ResolveDistributedCache((tenantId, _) =>
        {
            resolutions.AddOrUpdate(tenantId, 1, (_, n) => n + 1);
            return tenantId == options.DefaultTenantId ? defaultCache : null; // only the default tenant has its own store
        });

        using var pool = new TenantRedisConnectionPool(
            Microsoft.Extensions.Options.Options.Create(options),
            new ServiceCollection().BuildServiceProvider());

        // e.g. arbitrary tenant identifiers extracted from key prefixes
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 50; i++)
            {
                Assert.Same(defaultCache, pool.GetCacheForTenant($"unknown_{i}"));
            }
        }

        // The first unknown tenants stay memoized; the rest are resolved again instead of growing the pool forever
        Assert.Equal(1, resolutions["unknown_0"]);
        Assert.Equal(1, resolutions["unknown_1"]);
        Assert.Equal(3, resolutions["unknown_49"]);
        Assert.Equal(1, resolutions[options.DefaultTenantId]);
    }

    [Fact]
    public void GetCacheForTenant_ResolverThrows_RetriesOnNextCall()
    {
        var attempts = 0;
        var options = new MultiTenantRedisOptions();
        options.ResolveDistributedCache((_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("transient");
            }
            return new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        });

        using var pool = new TenantRedisConnectionPool(
            Microsoft.Extensions.Options.Options.Create(options),
            new ServiceCollection().BuildServiceProvider());

        Assert.Throws<InvalidOperationException>(() => pool.GetCacheForTenant("tenant_a"));
        Assert.IsAssignableFrom<IDistributedCache>(pool.GetCacheForTenant("tenant_a"));
        Assert.Equal(2, attempts);
    }
}
