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
