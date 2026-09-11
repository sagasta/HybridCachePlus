using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HybridCache.Plus.Tests.Integration;

public class CatalogCacheIntegrationTests
{
    private (ServiceProvider Provider, global::Microsoft.Extensions.Caching.Hybrid.HybridCache Cache, IProductRepository Repo, ProductRepository InnerRepo) CreateTestEnvironment()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();

        // Register real inner repository
        services.AddSingleton<ProductRepository>();
        services.AddSingleton<IProductRepository>(sp => sp.GetRequiredService<ProductRepository>());

        // Apply generated decorator
        services.DecorateProductRepositoryWithCache();

        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();
        var repo = sp.GetRequiredService<IProductRepository>();
        var innerRepo = sp.GetRequiredService<ProductRepository>();

        return (sp, cache, repo, innerRepo);
    }

    [Fact]
    public async Task GetProductAsync_CachesResult_L1HitAvoidsFactoryExecution()
    {
        var (_, cache, _, _) = CreateTestEnvironment();
        var factoryInvocations = 0;

        ValueTask<ProductDetailDto> Factory(CancellationToken ct)
        {
            factoryInvocations++;
            return ValueTask.FromResult(new ProductDetailDto("t1", 101, "Gaming Mouse", 49.99m));
        }

        // 1. Initial Call -> Cache Miss (Factory executes)
        var product1 = await cache.GetProductAsync("t1", 101, Factory);
        Assert.NotNull(product1);
        Assert.Equal("Gaming Mouse", product1.Name);
        Assert.Equal(1, factoryInvocations);

        // 2. Second Call -> Cache Hit in L1 (Factory does NOT execute)
        var product2 = await cache.GetProductAsync("t1", 101, Factory);
        Assert.NotNull(product2);
        Assert.Equal("Gaming Mouse", product2.Name);
        Assert.Equal(1, factoryInvocations); // Still 1!
    }

    [Fact]
    public async Task UpdateProductAsync_ViaDecorator_AutomaticallyInvalidatesCacheKey()
    {
        var (_, cache, repo, innerRepo) = CreateTestEnvironment();
        var factoryInvocations = 0;

        // Seed initial state in repo
        await innerRepo.UpdateProductAsync("t1", 200, "Original Keyboard", 99.99m);

        async ValueTask<ProductDetailDto> Factory(CancellationToken ct)
        {
            factoryInvocations++;
            var p = await innerRepo.GetByIdAsync("t1", 200, ct);
            return p!;
        }

        // 1. First read -> Miss (executes factory)
        var item1 = await cache.GetProductAsync("t1", 200, Factory);
        Assert.Equal("Original Keyboard", item1.Name);
        Assert.Equal(1, factoryInvocations);

        // 2. Second read -> Hit (L1)
        var item2 = await cache.GetProductAsync("t1", 200, Factory);
        Assert.Equal("Original Keyboard", item2.Name);
        Assert.Equal(1, factoryInvocations);

        // 3. Mutate repository via Decorator
        var updated = await repo.UpdateProductAsync("t1", 200, "Upgraded Mechanical Keyboard", 149.99m);
        Assert.Equal("Upgraded Mechanical Keyboard", updated.Name);

        // 4. Third read -> Miss! The decorator purged the key, so factory is re-executed
        var item3 = await cache.GetProductAsync("t1", 200, Factory);
        Assert.Equal("Upgraded Mechanical Keyboard", item3.Name);
        Assert.Equal(2, factoryInvocations); // Factory was re-executed!
    }

    [Fact]
    public async Task DeleteProductAsync_ViaDecorator_AutomaticallyInvalidatesCacheKey()
    {
        var (_, cache, repo, innerRepo) = CreateTestEnvironment();
        var factoryInvocations = 0;

        await innerRepo.UpdateProductAsync("t1", 300, "Monitor 4K", 399.99m);

        async ValueTask<ProductDetailDto> Factory(CancellationToken ct)
        {
            factoryInvocations++;
            var p = await innerRepo.GetByIdAsync("t1", 300, ct);
            return p ?? new ProductDetailDto("t1", 300, "DeletedPlaceholder", 0m);
        }

        // 1. Read to cache
        var item = await cache.GetProductAsync("t1", 300, Factory);
        Assert.Equal("Monitor 4K", item.Name);
        Assert.Equal(1, factoryInvocations);

        // 2. Hit in L1
        _ = await cache.GetProductAsync("t1", 300, Factory);
        Assert.Equal(1, factoryInvocations);

        // 3. Delete via Decorator
        await repo.DeleteProductAsync("t1", 300);

        // 4. Read again -> Must re-execute factory due to invalidation
        var afterDelete = await cache.GetProductAsync("t1", 300, Factory);
        Assert.Equal("DeletedPlaceholder", afterDelete.Name);
        Assert.Equal(2, factoryInvocations);
    }

    [Fact]
    public async Task EvictProductByTenantIdTagAsync_InvalidatesAllEntriesUnderTenant()
    {
        var (_, cache, _, _) = CreateTestEnvironment();
        var p1Invocations = 0;
        var p2Invocations = 0;

        ValueTask<ProductDetailDto> FactoryP1(CancellationToken ct)
        {
            p1Invocations++;
            return ValueTask.FromResult(new ProductDetailDto("tenantX", 1, "Headset", 79.99m));
        }

        ValueTask<ProductDetailDto> FactoryP2(CancellationToken ct)
        {
            p2Invocations++;
            return ValueTask.FromResult(new ProductDetailDto("tenantX", 2, "Webcam", 59.99m));
        }

        // Cache both items
        await cache.GetProductAsync("tenantX", 1, FactoryP1);
        await cache.GetProductAsync("tenantX", 2, FactoryP2);
        Assert.Equal(1, p1Invocations);
        Assert.Equal(1, p2Invocations);

        // Verify cache hits
        await cache.GetProductAsync("tenantX", 1, FactoryP1);
        await cache.GetProductAsync("tenantX", 2, FactoryP2);
        Assert.Equal(1, p1Invocations);
        Assert.Equal(1, p2Invocations);

        // Evict by tag "tenant:tenantX" using generated extension
        await cache.EvictProductByTenantIdTagAsync("tenantX");

        // Verify that both are evicted
        await cache.GetProductAsync("tenantX", 1, FactoryP1);
        await cache.GetProductAsync("tenantX", 2, FactoryP2);
        Assert.Equal(2, p1Invocations);
        Assert.Equal(2, p2Invocations);
    }

    [Fact]
    public async Task GetProductAsync_StateOverload_ZeroAllocationExecution()
    {
        var (_, cache, _, _) = CreateTestEnvironment();

        var product = await cache.GetProductAsync(
            "t1",
            999,
            "StatePayload",
            static (state, ct) => ValueTask.FromResult(new ProductDetailDto("t1", 999, state, 10m)));

        Assert.Equal("StatePayload", product.Name);
    }

    [Fact]
    public async Task GetProductAsync_WithPolicyName_AppliesConfiguredPolicyAndTenantTtl()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();

        services.AddHybridCachePlus(builder =>
        {
            // Global policy for "CatalogProducts" template
            builder.ConfigurePolicy("CatalogProducts", policy =>
            {
                policy.LocalTtlSeconds = 120;
                policy.DistributedTtlSeconds = 1200;
            });

            // Tenant-specific override for "vip_short_ttl"
            builder.ConfigureTenantPolicy("vip_short_ttl", "CatalogProducts", policy =>
            {
                policy.LocalTtlSeconds = 1;
                policy.DistributedTtlSeconds = 5;
            });
        });

        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        var vipInvocations = 0;
        ValueTask<ProductDetailDto> VipFactory(CancellationToken ct)
        {
            vipInvocations++;
            return ValueTask.FromResult(new ProductDetailDto("vip_short_ttl", 501, "VIP Short TTL Item", 10m));
        }

        var standardInvocations = 0;
        ValueTask<ProductDetailDto> StandardFactory(CancellationToken ct)
        {
            standardInvocations++;
            return ValueTask.FromResult(new ProductDetailDto("standard_tenant", 502, "Standard Item", 20m));
        }

        // 1. Initial call for VIP -> Miss (Factory runs, count = 1)
        var vip1 = await cache.GetProductAsync("vip_short_ttl", 501, VipFactory);
        Assert.Equal(1, vipInvocations);

        // 2. Immediate second call for VIP -> Hit in L1 (Factory does not run, count = 1)
        var vip2 = await cache.GetProductAsync("vip_short_ttl", 501, VipFactory);
        Assert.Equal(1, vipInvocations);

        // 3. Initial call for Standard tenant -> Miss (Factory runs, count = 1)
        var std1 = await cache.GetProductAsync("standard_tenant", 502, StandardFactory);
        Assert.Equal(1, standardInvocations);

        // 4. Immediate second call for Standard tenant -> Hit in L1 (Factory does not run, count = 1)
        var std2 = await cache.GetProductAsync("standard_tenant", 502, StandardFactory);
        Assert.Equal(1, standardInvocations);

        // 5. Wait 1.1 seconds: VIP tenant has LocalTtl = 1s, so it MUST expire!
        // Standard tenant has LocalTtl = 120s, so it must NOT expire!
        await Task.Delay(1100);

        // 6. VIP third call -> Cache expired due to tenant policy override! Factory re-runs (count = 2)
        var vip3 = await cache.GetProductAsync("vip_short_ttl", 501, VipFactory);
        Assert.Equal(2, vipInvocations);

        // 7. Standard tenant third call -> Cache still valid due to global policy (120s)! Factory does NOT re-run (count = 1)
        var std3 = await cache.GetProductAsync("standard_tenant", 502, StandardFactory);
        Assert.Equal(1, standardInvocations);
    }
}
