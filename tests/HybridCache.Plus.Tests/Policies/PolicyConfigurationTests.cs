using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using HybridCache.Plus.Policies;

namespace HybridCache.Plus.Tests.Policies;

[Collection("StaticStateTests")]
public class PolicyConfigurationTests
{
    private static readonly HybridCacheEntryOptions Fallback = new()
    {
        LocalCacheExpiration = TimeSpan.FromSeconds(1),
        Expiration = TimeSpan.FromSeconds(2)
    };

    private static IConfigurationRoot BuildConfiguration(int localTtlSeconds, int vipLocalTtlSeconds) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HybridCachePlus:Policies:CatalogProducts:LocalTtlSeconds"] = localTtlSeconds.ToString(),
                ["HybridCachePlus:Policies:CatalogProducts:DistributedTtlSeconds"] = "600",
                ["HybridCachePlus:Tenants:tenant_vip:Policies:CatalogProducts:LocalTtlSeconds"] = vipLocalTtlSeconds.ToString(),
            })
            .Build();

    private static async Task<ServiceProvider> StartHostAsync(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddHybridCachePlus(builder => builder.ConfigurePolicies(configuration.GetSection("HybridCachePlus")));

        var provider = services.BuildServiceProvider();
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
        return provider;
    }

    [Fact]
    public async Task ConfigurationSection_AppliesGlobalAndTenantPolicies()
    {
        await using var provider = await StartHostAsync(BuildConfiguration(localTtlSeconds: 60, vipLocalTtlSeconds: 10));
        var cache = provider.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        var global = HybridCachePlusPolicyRegistry.Resolve(cache, "CatalogProducts", null, Fallback);
        var vip = HybridCachePlusPolicyRegistry.Resolve(cache, "CatalogProducts", "TENANT_VIP", Fallback);

        Assert.Equal(TimeSpan.FromSeconds(60), global.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(600), global.Expiration);
        Assert.Equal(TimeSpan.FromSeconds(10), vip.LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(600), vip.Expiration);
    }

    [Fact]
    public async Task ConfigurationReload_UpdatesResolvedPoliciesWithoutRestart()
    {
        var configuration = BuildConfiguration(localTtlSeconds: 60, vipLocalTtlSeconds: 10);
        await using var provider = await StartHostAsync(configuration);
        var cache = provider.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        Assert.Equal(TimeSpan.FromSeconds(60), HybridCachePlusPolicyRegistry.Resolve(cache, "CatalogProducts", null, Fallback).LocalCacheExpiration);

        configuration["HybridCachePlus:Policies:CatalogProducts:LocalTtlSeconds"] = "5";
        configuration.Reload();

        Assert.Equal(TimeSpan.FromSeconds(5), HybridCachePlusPolicyRegistry.Resolve(cache, "CatalogProducts", null, Fallback).LocalCacheExpiration);
    }

    [Fact]
    public async Task TwoHostsInOneProcess_EachCacheResolvesItsOwnPolicies()
    {
        await using var providerA = await StartHostAsync(BuildConfiguration(localTtlSeconds: 11, vipLocalTtlSeconds: 1));
        await using var providerB = await StartHostAsync(BuildConfiguration(localTtlSeconds: 22, vipLocalTtlSeconds: 2));

        var cacheA = providerA.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();
        var cacheB = providerB.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        Assert.Equal(TimeSpan.FromSeconds(11), HybridCachePlusPolicyRegistry.Resolve(cacheA, "CatalogProducts", null, Fallback).LocalCacheExpiration);
        Assert.Equal(TimeSpan.FromSeconds(22), HybridCachePlusPolicyRegistry.Resolve(cacheB, "CatalogProducts", null, Fallback).LocalCacheExpiration);
    }
}
