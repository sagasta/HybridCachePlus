using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using HybridCache.Plus.Backplane;
using HybridCache.Plus.Backplane.Redis;
using HybridCache.Plus.Tests.Integration;

namespace HybridCache.Plus.Tests.Backplane;

public class RedisBackplaneIntegrationTests
{
    [Fact]
    public void BackplaneMessage_NativeAotSerialization_RoundtripsAccurately()
    {
        var original = new BackplaneEvictionMessage(EvictType.ByExactKey, "tenants:t1:products:42", "pod-alpha-123");

        byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(original, BackplaneJsonContext.Default.BackplaneEvictionMessage);
        Assert.NotEmpty(utf8Bytes);

        var roundtripped = JsonSerializer.Deserialize(utf8Bytes.AsSpan(), BackplaneJsonContext.Default.BackplaneEvictionMessage);

        Assert.Equal(original.EvictType, roundtripped.EvictType);
        Assert.Equal(original.Target, roundtripped.Target);
        Assert.Equal(original.OriginInstanceId, roundtripped.OriginInstanceId);
    }

    [Fact]
    public void BackplaneMessage_TagSerialization_RoundtripsAccurately()
    {
        var original = BackplaneEvictionMessage.CreateTag("tenant:corp_99", "pod-beta-456");

        byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(original, BackplaneJsonContext.Default.BackplaneEvictionMessage);
        var roundtripped = JsonSerializer.Deserialize(utf8Bytes.AsSpan(), BackplaneJsonContext.Default.BackplaneEvictionMessage);

        Assert.Equal(EvictType.ByTag, roundtripped.EvictType);
        Assert.Equal("tenant:corp_99", roundtripped.Target);
        Assert.Equal("pod-beta-456", roundtripped.OriginInstanceId);
    }

    [Fact]
    public async Task MultiInstance_RepositoryMutationInInstanceA_PurgesL1CacheInInstanceB()
    {
        // 1. Setup shared in-memory Redis message broker
        var broker = new InMemoryRedisBroker();

        // 2. Build Instance A (Pod A)
        var (providerA, cacheA, repoA, innerRepoA) = CreatePodInstance("pod-A", broker);

        // 3. Build Instance B (Pod B)
        var (providerB, cacheB, repoB, innerRepoB) = CreatePodInstance("pod-B", broker);

        // Start background workers for both pods
        var hostedServicesA = providerA.GetServices<IHostedService>();
        foreach (var svc in hostedServicesA) await svc.StartAsync(CancellationToken.None);

        var hostedServicesB = providerB.GetServices<IHostedService>();
        foreach (var svc in hostedServicesB) await svc.StartAsync(CancellationToken.None);

        try
        {
            const string tenantId = "tenant_global";
            const long productId = 500;

            // Initial state in repository
            await innerRepoA.UpdateProductAsync(tenantId, productId, "Original Laptop", 1000m);
            await innerRepoB.UpdateProductAsync(tenantId, productId, "Original Laptop", 1000m);

            // Pod A caches product 500
            var p1 = await cacheA.GetProductAsync(tenantId, productId, async _ =>
                (await innerRepoA.GetByIdAsync(tenantId, productId))!);
            Assert.Equal("Original Laptop", p1.Name);

            // Pod B caches product 500 into its own L1 memory
            var pB1 = await cacheB.GetProductAsync(tenantId, productId, async _ =>
                (await innerRepoB.GetByIdAsync(tenantId, productId))!);
            Assert.Equal("Original Laptop", pB1.Name);

            // 4. Pod A executes a mutation through the decorated repository
            // This mutates innerRepoA and triggers automated cache invalidation + backplane broadcast
            await repoA.UpdateProductAsync(tenantId, productId, "Updated Laptop V2", 1200m);

            // Sync innerRepoB so DB representation matches
            await innerRepoB.UpdateProductAsync(tenantId, productId, "Updated Laptop V2", 1200m);

            // 5. Allow brief async dispatch across the in-memory broker to Pod B
            await Task.Delay(250);

            // 6. Pod B reads product 500. If L1 was purged by the backplane worker,
            // the factory must be invoked and return "Updated Laptop V2", NOT the stale "Original Laptop"
            var pB2 = await cacheB.GetProductAsync(tenantId, productId, async _ =>
                (await innerRepoB.GetByIdAsync(tenantId, productId))!);

            Assert.Equal("Updated Laptop V2", pB2.Name);
            Assert.Equal(1200m, pB2.Price);
        }
        finally
        {
            foreach (var svc in hostedServicesA) await svc.StopAsync(CancellationToken.None);
            foreach (var svc in hostedServicesB) await svc.StopAsync(CancellationToken.None);
            await providerA.DisposeAsync();
            await providerB.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultiInstance_ExplicitEvictInInstanceA_PurgesL1CacheInInstanceB()
    {
        var broker = new InMemoryRedisBroker();
        var (providerA, cacheA, _, _) = CreatePodInstance("pod-A", broker);
        var (providerB, cacheB, _, innerRepoB) = CreatePodInstance("pod-B", broker);

        var hostedServicesA = providerA.GetServices<IHostedService>();
        foreach (var svc in hostedServicesA) await svc.StartAsync(CancellationToken.None);

        var hostedServicesB = providerB.GetServices<IHostedService>();
        foreach (var svc in hostedServicesB) await svc.StartAsync(CancellationToken.None);

        try
        {
            const string tenantId = "t_100";
            const long productId = 200;
            var calls = 0;

            // Load into Pod B's L1
            var valB = await cacheB.GetProductAsync(tenantId, productId, _ =>
            {
                calls++;
                return ValueTask.FromResult(new ProductDetailDto(tenantId, productId, $"Item-{calls}", 50m));
            });
            Assert.Equal("Item-1", valB.Name);
            Assert.Equal(1, calls);

            // Reading again from Pod B returns cached L1 item without calling factory
            var valB_Cached = await cacheB.GetProductAsync(tenantId, productId, _ =>
            {
                calls++;
                return ValueTask.FromResult(new ProductDetailDto(tenantId, productId, $"Item-{calls}", 50m));
            });
            Assert.Equal("Item-1", valB_Cached.Name);
            Assert.Equal(1, calls);

            // Pod A explicitly calls generated EvictProductAsync
            await cacheA.EvictProductAsync(tenantId, productId);

            // Allow event to travel over backplane
            await Task.Delay(250);

            // Pod B's L1 should now be evicted! Factory must be called again!
            var valB_Fresh = await cacheB.GetProductAsync(tenantId, productId, _ =>
            {
                calls++;
                return ValueTask.FromResult(new ProductDetailDto(tenantId, productId, $"Item-{calls}", 50m));
            });
            Assert.Equal("Item-2", valB_Fresh.Name);
            Assert.Equal(2, calls);
        }
        finally
        {
            foreach (var svc in hostedServicesA) await svc.StopAsync(CancellationToken.None);
            foreach (var svc in hostedServicesB) await svc.StopAsync(CancellationToken.None);
            await providerA.DisposeAsync();
            await providerB.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultiInstance_TagEvictionInInstanceA_PurgesAllMatchingTagEntriesInInstanceB()
    {
        var broker = new InMemoryRedisBroker();
        var (providerA, cacheA, _, _) = CreatePodInstance("pod-A", broker);
        var (providerB, cacheB, _, _) = CreatePodInstance("pod-B", broker);

        var hostedServicesA = providerA.GetServices<IHostedService>();
        foreach (var svc in hostedServicesA) await svc.StartAsync(CancellationToken.None);

        var hostedServicesB = providerB.GetServices<IHostedService>();
        foreach (var svc in hostedServicesB) await svc.StartAsync(CancellationToken.None);

        try
        {
            const string tenantId = "tenant_tag_test";
            var factoryCallsP1 = 0;
            var factoryCallsP2 = 0;

            // Cache product 1 and product 2 on Pod B under tenant_tag_test
            await cacheB.GetProductAsync(tenantId, 1, _ =>
            {
                factoryCallsP1++;
                return ValueTask.FromResult(new ProductDetailDto(tenantId, 1, "P1", 10m));
            });
            await cacheB.GetProductAsync(tenantId, 2, _ =>
            {
                factoryCallsP2++;
                return ValueTask.FromResult(new ProductDetailDto(tenantId, 2, "P2", 20m));
            });

            Assert.Equal(1, factoryCallsP1);
            Assert.Equal(1, factoryCallsP2);

            // Pod A invalidates by tag
            await cacheA.EvictProductByTenantIdTagAsync(tenantId);

            // Wait for backplane event
            await Task.Delay(250);

            // Both entries on Pod B should now require re-fetching
            await cacheB.GetProductAsync(tenantId, 1, _ =>
            {
                factoryCallsP1++;
                return ValueTask.FromResult(new ProductDetailDto(tenantId, 1, "P1_New", 15m));
            });
            await cacheB.GetProductAsync(tenantId, 2, _ =>
            {
                factoryCallsP2++;
                return ValueTask.FromResult(new ProductDetailDto(tenantId, 2, "P2_New", 25m));
            });

            Assert.Equal(2, factoryCallsP1);
            Assert.Equal(2, factoryCallsP2);
        }
        finally
        {
            foreach (var svc in hostedServicesA) await svc.StopAsync(CancellationToken.None);
            foreach (var svc in hostedServicesB) await svc.StopAsync(CancellationToken.None);
            await providerA.DisposeAsync();
            await providerB.DisposeAsync();
        }
    }

    [Fact]
    public async Task MultiInstance_EchoFromSameOrigin_IsDiscardedWithoutEvicting()
    {
        var broker = new InMemoryRedisBroker();
        var (providerA, cacheA, _, _) = CreatePodInstance("pod-A", broker);

        var hostedServicesA = providerA.GetServices<IHostedService>();
        foreach (var svc in hostedServicesA) await svc.StartAsync(CancellationToken.None);

        try
        {
            const string tenantId = "tenant_echo";
            const long productId = 777;
            var factoryCalls = 0;

            // Cache on Pod A
            await cacheA.GetProductAsync(tenantId, productId, _ =>
            {
                factoryCalls++;
                return ValueTask.FromResult(new ProductDetailDto(tenantId, productId, "EchoTest", 100m));
            });
            Assert.Equal(1, factoryCalls);

            // Directly publish an eviction event originating from "pod-A" (simulating echo)
            var echoMessage = BackplaneEvictionMessage.CreateKey($"tenants:{tenantId}:products:{productId}", "pod-A");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(echoMessage, BackplaneJsonContext.Default.BackplaneEvictionMessage);
            await broker.PublishAsync(StackExchange.Redis.RedisChannel.Literal("test:evictions"), (StackExchange.Redis.RedisValue)bytes);

            // Wait for worker processing
            await Task.Delay(250);

            // Read again: Pod A should NOT have evicted because it ignored its own OriginInstanceId
            await cacheA.GetProductAsync(tenantId, productId, _ =>
            {
                factoryCalls++;
                return ValueTask.FromResult(new ProductDetailDto(tenantId, productId, "ShouldNotCallFactory", 100m));
            });

            // Factory should NOT have been called again (still 1)
            Assert.Equal(1, factoryCalls);
        }
        finally
        {
            foreach (var svc in hostedServicesA) await svc.StopAsync(CancellationToken.None);
            await providerA.DisposeAsync();
        }
    }

    private static (ServiceProvider Provider, global::Microsoft.Extensions.Caching.Hybrid.HybridCache Cache, IProductRepository Repo, ProductRepository InnerRepo)
        CreatePodInstance(string instanceId, InMemoryRedisBroker broker)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddHybridCache();

        // Register HybridCache.Plus with Redis backplane
        services.AddHybridCachePlus(options =>
        {
            options.UseRedisBackplane(redis =>
            {
                redis.InstanceId = instanceId;
                redis.ChannelName = "test:evictions";
                redis.ConnectionMultiplexer = broker.CreateMultiplexer();
            });
        });

        // Register repositories and generated decorator
        services.AddSingleton<ProductRepository>();
        services.AddSingleton<IProductRepository>(sp => sp.GetRequiredService<ProductRepository>());
        services.DecorateProductRepositoryWithCache();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();
        var repo = provider.GetRequiredService<IProductRepository>();
        var innerRepo = provider.GetRequiredService<ProductRepository>();

        return (provider, cache, repo, innerRepo);
    }
}
