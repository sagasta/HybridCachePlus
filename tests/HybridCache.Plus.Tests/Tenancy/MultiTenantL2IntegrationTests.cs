using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using HybridCache.Plus.Tenancy;
using HybridCache.Plus.Tenancy.Redis;
using HybridCache.Plus.Tests.Integration;

namespace HybridCache.Plus.Tests.Tenancy;

public class MultiTenantL2IntegrationTests
{
    [Fact]
    public async Task MultiTenant_RoutingToDedicatedL2Caches_IsolatesDataPerTenant()
    {
        var tenantCaches = new ConcurrentDictionary<string, MemoryDistributedCache>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();

        services.AddHybridCachePlus(options =>
        {
            options.UseMultiTenantRedisL2(tenant =>
            {
                tenant.ResolveDistributedCache((tenantId, sp) =>
                {
                    return tenantCaches.GetOrAdd(tenantId, _ =>
                        new MemoryDistributedCache(global::Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));
                });
                tenant.EnableKeyPrefixTenantExtraction = true;
            });
        });

        // Register repository and decorator
        services.AddSingleton<ProductRepository>();
        services.AddSingleton<IProductRepository>(sp => sp.GetRequiredService<ProductRepository>());
        services.DecorateProductRepositoryWithCache();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();
        var tenantAccessor = provider.GetRequiredService<ITenantContextAccessor>();

        // 1. Tenant Alpha writes product 10
        tenantAccessor.CurrentTenantId = "tenant_alpha";
        var productAlpha = await cache.GetProductAsync("tenant_alpha", 10, _ =>
            ValueTask.FromResult(new ProductDetailDto("tenant_alpha", 10, "Alpha Laptop", 1500m)));

        // 2. Tenant Beta writes product 10
        tenantAccessor.CurrentTenantId = "tenant_beta";
        var productBeta = await cache.GetProductAsync("tenant_beta", 10, _ =>
            ValueTask.FromResult(new ProductDetailDto("tenant_beta", 10, "Beta Phone", 800m)));

        Assert.Equal("Alpha Laptop", productAlpha.Name);
        Assert.Equal("Beta Phone", productBeta.Name);

        // 3. Verify L2 isolation: Alpha's L2 distributed cache exists and Beta's exists separately
        Assert.True(tenantCaches.ContainsKey("tenant_alpha"));
        Assert.True(tenantCaches.ContainsKey("tenant_beta"));

        var alphaL2 = tenantCaches["tenant_alpha"];
        var betaL2 = tenantCaches["tenant_beta"];

        // In L2, Alpha's cache has Alpha's key and NOT Beta's key
        var alphaKey = "tenants:tenant_alpha:products:10";
        var betaKey = "tenants:tenant_beta:products:10";

        var alphaBytesInAlphaL2 = await alphaL2.GetAsync(alphaKey);
        var betaBytesInAlphaL2 = await alphaL2.GetAsync(betaKey);
        var betaBytesInBetaL2 = await betaL2.GetAsync(betaKey);
        var alphaBytesInBetaL2 = await betaL2.GetAsync(alphaKey);

        Assert.NotNull(alphaBytesInAlphaL2);
        Assert.Null(betaBytesInAlphaL2); // Tenant Beta's data is NOT in Alpha's L2
        Assert.NotNull(betaBytesInBetaL2);
        Assert.Null(alphaBytesInBetaL2); // Tenant Alpha's data is NOT in Beta's L2
    }

    [Fact]
    public async Task MultiTenant_SameEntityIdAcrossTenants_NoCollisionInSharedL1()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();

        services.AddHybridCachePlus(options =>
        {
            options.UseMultiTenantRedisL2(tenant =>
            {
                tenant.EnableKeyPrefixTenantExtraction = true;
            });
        });

        services.AddSingleton<ProductRepository>();
        services.AddSingleton<IProductRepository>(sp => sp.GetRequiredService<ProductRepository>());
        services.DecorateProductRepositoryWithCache();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        const long commonProductId = 42;

        // Populate L1 for Tenant 1
        var p1 = await cache.GetProductAsync("tenant_1", commonProductId, _ =>
            ValueTask.FromResult(new ProductDetailDto("tenant_1", commonProductId, "T1 Entity", 100m)));

        // Populate L1 for Tenant 2
        var p2 = await cache.GetProductAsync("tenant_2", commonProductId, _ =>
            ValueTask.FromResult(new ProductDetailDto("tenant_2", commonProductId, "T2 Entity", 200m)));

        Assert.Equal("T1 Entity", p1.Name);
        Assert.Equal("T2 Entity", p2.Name);

        // Re-read from L1 without invoking factory (using dummy factory that throws if called)
        var p1_cached = await cache.GetProductAsync("tenant_1", commonProductId, _ =>
            throw new InvalidOperationException("Factory should not be invoked on L1 hit"));

        var p2_cached = await cache.GetProductAsync("tenant_2", commonProductId, _ =>
            throw new InvalidOperationException("Factory should not be invoked on L1 hit"));

        Assert.Equal("T1 Entity", p1_cached.Name);
        Assert.Equal(100m, p1_cached.Price);
        Assert.Equal("T2 Entity", p2_cached.Name);
        Assert.Equal(200m, p2_cached.Price);
    }

    [Fact]
    public async Task MultiTenant_AntiStampedeProtection_ExecutesFactoryExactlyOncePerKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();

        services.AddHybridCachePlus(options =>
        {
            options.UseMultiTenantRedisL2(tenant =>
            {
                tenant.EnableKeyPrefixTenantExtraction = true;
            });
        });

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        const string tenantId = "tenant_stampede";
        const long productId = 999;
        var factoryExecutionCount = 0;

        // Launch 20 concurrent tasks all attempting to retrieve the exact same uncached key simultaneously
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            return await cache.GetProductAsync(tenantId, productId, async ct =>
            {
                // Simulate I/O latency to ensure all 20 threads overlap
                await Task.Delay(50, ct);
                Interlocked.Increment(ref factoryExecutionCount);
                return new ProductDetailDto(tenantId, productId, "Stampede Protected Item", 99.99m);
            });
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        // Anti-stampede guarantee: exactly 1 execution of the expensive factory delegate
        Assert.Equal(1, factoryExecutionCount);

        // All 20 callers received the identical valid result
        Assert.Equal(20, results.Length);
        foreach (var result in results)
        {
            Assert.Equal("Stampede Protected Item", result.Name);
            Assert.Equal(99.99m, result.Price);
        }
    }
}
