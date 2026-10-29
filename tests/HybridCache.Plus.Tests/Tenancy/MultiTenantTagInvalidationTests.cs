using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using HybridCache.Plus.Tenancy;

namespace HybridCache.Plus.Tests.Tenancy;

public class MultiTenantTagInvalidationTests
{
    private const string Key = "tenants:t1:products:1";
    private static readonly string[] Tags = ["tenant:t1"];

    private sealed class Pod(Microsoft.Extensions.Caching.Hybrid.HybridCache cache, ITenantContextAccessor? tenant)
    {
        public Microsoft.Extensions.Caching.Hybrid.HybridCache Cache { get; } = cache;
        public ITenantContextAccessor? Tenant { get; } = tenant;
    }

    private static Pod CreateMultiTenantPod(ConcurrentDictionary<string, IDistributedCache> stores)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddHybridCachePlus(plus => plus.UseMultiTenantRedisL2(t => t.ResolveDistributedCache((id, _) =>
            stores.GetOrAdd(id, _ => new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()))))));
        var sp = services.BuildServiceProvider();
        return new Pod(sp.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(), sp.GetRequiredService<ITenantContextAccessor>());
    }

    private static Pod CreateSharedL2Pod(IDistributedCache shared)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(shared);
        services.AddHybridCache();
        return new Pod(services.BuildServiceProvider().GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(), null);
    }

    private static async Task<string> RunScenarioAsync(Pod podA, Pod podB, string? evictingTenant, string? readingTenant)
    {
        var calls = 0;
        ValueTask<string> Factory(CancellationToken _) => ValueTask.FromResult($"v{++calls}");

        await podA.Cache.GetOrCreateAsync(Key, Factory, tags: Tags);
        await Task.Delay(100);

        // Mutation handled inside a request of tenant t1 (or a background job when null)
        if (podA.Tenant != null) podA.Tenant.CurrentTenantId = evictingTenant;
        await podA.Cache.RemoveByTagAsync("tenant:t1");
        if (podA.Tenant != null) podA.Tenant.CurrentTenantId = null;
        await Task.Delay(100);

        // Fresh pod (empty L1) reads afterwards
        if (podB.Tenant != null) podB.Tenant.CurrentTenantId = readingTenant;
        try
        {
            return await podB.Cache.GetOrCreateAsync(Key, Factory, tags: Tags);
        }
        finally
        {
            if (podB.Tenant != null) podB.Tenant.CurrentTenantId = null;
        }
    }

    [Fact]
    public async Task TenantPrefixedKey_IsStoredInItsTenantL2_EvenUnderAnotherAmbientTenant()
    {
        var stores = new ConcurrentDictionary<string, IDistributedCache>(StringComparer.OrdinalIgnoreCase);
        var pod = CreateMultiTenantPod(stores);

        // e.g. an admin request of tenant t2 loading data of tenant t1
        pod.Tenant!.CurrentTenantId = "t2";
        try
        {
            await pod.Cache.GetOrCreateAsync(Key, _ => ValueTask.FromResult("t1-data"));
        }
        finally
        {
            pod.Tenant.CurrentTenantId = null;
        }
        await Task.Delay(100);

        Assert.NotNull(await stores["t1"].GetAsync(Key));
        Assert.False(stores.TryGetValue("t2", out var t2Store) && await t2Store.GetAsync(Key) != null,
            "Tenant t1's data must not be written into tenant t2's L2.");
    }

    [Fact]
    public async Task Baseline_SingleSharedL2_TagEvictionIsSeenByAnotherPod()
    {
        var shared = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var result = await RunScenarioAsync(CreateSharedL2Pod(shared), CreateSharedL2Pod(shared), null, null);
        Assert.NotEqual("v1", result);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("t1", "t1")]
    [InlineData("t1", null)]
    [InlineData(null, "t1")]
    public async Task MultiTenant_TagEvictionIsSeenByAnotherPod_RegardlessOfAmbientTenant(string? evictingTenant, string? readingTenant)
    {
        var stores = new ConcurrentDictionary<string, IDistributedCache>(StringComparer.OrdinalIgnoreCase);
        var result = await RunScenarioAsync(CreateMultiTenantPod(stores), CreateMultiTenantPod(stores), evictingTenant, readingTenant);
        Assert.NotEqual("v1", result);
    }
}
