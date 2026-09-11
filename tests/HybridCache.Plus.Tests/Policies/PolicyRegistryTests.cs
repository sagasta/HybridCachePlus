using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using HybridCache.Plus.Options;
using HybridCache.Plus.Policies;
using HybridCache.Plus.Tenancy;
using HybridCache.Plus.Tests.Integration;

namespace HybridCache.Plus.Tests.Policies;

public class PolicyRegistryTests
{
    [Fact]
    public void Resolve_NoConfigurationOverrides_ReturnsFallbackDirectly()
    {
        var registry = new HybridCachePlusPolicyRegistry(new HybridCachePlusPolicyOptions());

        var fallback = new HybridCacheEntryOptions
        {
            LocalCacheExpiration = TimeSpan.FromSeconds(60),
            Expiration = TimeSpan.FromSeconds(600)
        };

        var resolved = registry.ResolveOptions("CatalogProducts", "tenant_standard", fallback);

        Assert.Same(fallback, resolved);
        Assert.Equal(TimeSpan.FromSeconds(60), resolved.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(600), resolved.Expiration);
    }

    [Fact]
    public void Resolve_GlobalPolicyOverride_AppliesConfiguredTtls()
    {
        var options = new HybridCachePlusPolicyOptions();
        options.Policies["CatalogProducts"] = new CachePolicyOptions
        {
            LocalTtlSeconds = 120,
            DistributedTtlSeconds = 1800
        };

        var registry = new HybridCachePlusPolicyRegistry(options);

        var fallback = new HybridCacheEntryOptions
        {
            LocalCacheExpiration = TimeSpan.FromSeconds(60),
            Expiration = TimeSpan.FromSeconds(600)
        };

        var resolved = registry.ResolveOptions("CatalogProducts", null, fallback);

        Assert.NotSame(fallback, resolved);
        Assert.Equal(TimeSpan.FromSeconds(120), resolved.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(1800), resolved.Expiration);
    }

    [Fact]
    public void Resolve_TenantSpecificOverride_TakesPrecedenceOverGlobalPolicy()
    {
        var options = new HybridCachePlusPolicyOptions();

        // Global policy
        options.Policies["CatalogProducts"] = new CachePolicyOptions
        {
            LocalTtlSeconds = 60,
            DistributedTtlSeconds = 600
        };

        // Tenant-specific policy for VIP tenant
        var vipTenant = new TenantPolicyOptions();
        vipTenant.Policies["CatalogProducts"] = new CachePolicyOptions
        {
            LocalTtlSeconds = 10,
            DistributedTtlSeconds = 60
        };
        options.Tenants["tenant_vip"] = vipTenant;

        // Tenant-specific policy for Free tenant
        var freeTenant = new TenantPolicyOptions();
        freeTenant.Policies["CatalogProducts"] = new CachePolicyOptions
        {
            LocalTtlSeconds = 600,
            DistributedTtlSeconds = 86400
        };
        options.Tenants["tenant_free"] = freeTenant;

        var registry = new HybridCachePlusPolicyRegistry(options);

        var fallback = new HybridCacheEntryOptions
        {
            LocalCacheExpiration = TimeSpan.FromSeconds(30),
            Expiration = TimeSpan.FromSeconds(300)
        };

        // 1. VIP Tenant resolution
        var vipResolved = registry.ResolveOptions("CatalogProducts", "tenant_vip", fallback);
        Assert.Equal(TimeSpan.FromSeconds(10), vipResolved.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(60), vipResolved.Expiration);

        // 2. Free Tenant resolution
        var freeResolved = registry.ResolveOptions("CatalogProducts", "tenant_free", fallback);
        Assert.Equal(TimeSpan.FromSeconds(600), freeResolved.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(86400), freeResolved.Expiration);

        // 3. Regular Tenant without override -> falls back to Global policy
        var standardResolved = registry.ResolveOptions("CatalogProducts", "tenant_standard", fallback);
        Assert.Equal(TimeSpan.FromSeconds(60), standardResolved.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(600), standardResolved.Expiration);
    }

    [Fact]
    public void Resolve_AmbientTenantAccessor_IsUsedWhenTenantIdParameterIsNull()
    {
        var options = new HybridCachePlusPolicyOptions();
        var bankingTenant = new TenantPolicyOptions();
        bankingTenant.Policies["CatalogProducts"] = new CachePolicyOptions
        {
            LocalTtlSeconds = 15,
            DistributedTtlSeconds = 90
        };
        options.Tenants["tenant_banking"] = bankingTenant;

        var registry = new HybridCachePlusPolicyRegistry(options);
        HybridCachePlusPolicyRegistry.Current = registry;

        var accessor = new AsyncLocalTenantContextAccessor { CurrentTenantId = "tenant_banking" };
        HybridCachePlusPolicyRegistry.SetAmbientTenantAccessor(accessor);

        var fallback = new HybridCacheEntryOptions
        {
            LocalCacheExpiration = TimeSpan.FromSeconds(60),
            Expiration = TimeSpan.FromSeconds(600)
        };

        // tenantId parameter is null, but ambient context has "tenant_banking"
        var resolved = HybridCachePlusPolicyRegistry.Resolve("CatalogProducts", null, fallback);

        Assert.Equal(TimeSpan.FromSeconds(15), resolved.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(90), resolved.Expiration);
    }

    [Fact]
    public async Task DiBuilder_ConfigureTenantPolicy_IntegratesWithGeneratedExtensionMethod()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();

        services.AddHybridCachePlus(builder =>
        {
            builder.ConfigurePolicy("ICatalogCache.GetProductAsync", policy =>
            {
                policy.LocalTtlSeconds = 45;
                policy.DistributedTtlSeconds = 450;
            });

            builder.ConfigureTenantPolicy("vip_customer", "ICatalogCache.GetProductAsync", policy =>
            {
                policy.LocalTtlSeconds = 5;
                policy.DistributedTtlSeconds = 25;
            });
        });

        var sp = services.BuildServiceProvider();
        var cache = sp.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        // 1. VIP Customer call
        var vipProduct = await cache.GetProductAsync("vip_customer", 1001, ct =>
            ValueTask.FromResult(new ProductDetailDto("vip_customer", 1001, "VIP Item", 100m)));
        Assert.NotNull(vipProduct);

        var vipOptions = HybridCachePlusPolicyRegistry.Resolve("ICatalogCache.GetProductAsync", "vip_customer", new HybridCacheEntryOptions());
        Assert.Equal(TimeSpan.FromSeconds(5), vipOptions.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(25), vipOptions.Expiration);

        // 2. Standard Customer call
        var standardProduct = await cache.GetProductAsync("standard_customer", 1002, ct =>
            ValueTask.FromResult(new ProductDetailDto("standard_customer", 1002, "Standard Item", 50m)));
        Assert.NotNull(standardProduct);

        var standardOptions = HybridCachePlusPolicyRegistry.Resolve("ICatalogCache.GetProductAsync", "standard_customer", new HybridCacheEntryOptions());
        Assert.Equal(TimeSpan.FromSeconds(45), standardOptions.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(450), standardOptions.Expiration);
    }
}
