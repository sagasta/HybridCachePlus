using HybridCache.Plus;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HybridCache.Plus.PackageSmokeTest;

public record OrderDto(int OrderId, string Customer, decimal Total);

public interface IOrderService
{
    Task<OrderDto> UpdateOrderAsync(int orderId, string customer, decimal newTotal);
}

public class OrderService : IOrderService
{
    public Task<OrderDto> UpdateOrderAsync(int orderId, string customer, decimal newTotal) =>
        Task.FromResult(new OrderDto(orderId, customer, newTotal));
}

[HybridCacheKeys]
public partial interface IOrderCache
{
    [CacheTemplate("orders:{orderId}", Tags = ["customer:{customer}"])]
    [InvalidatedBy<IOrderService>(nameof(IOrderService.UpdateOrderAsync))]
    ValueTask<OrderDto> GetOrderAsync(int orderId, string customer);
}

public class PackageSmokeTests
{
    [Fact]
    public void SourceGenerator_RunsFromNugetPackage_AndGeneratesExtensionAndDecorator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddHybridCachePlus(plus =>
        {
            plus.UseRedisBackplane(b => b.Configuration = "127.0.0.1:6379,abortConnect=false,connectTimeout=100");
            plus.UseMultiTenantRedisL2(t => t.ResolveConnectionString(_ => "127.0.0.1:6379,abortConnect=false,connectTimeout=100"));
        });

        // Register dummy service and decorate with generated method
        services.AddSingleton<OrderService>();
        services.AddSingleton<IOrderService>(sp => sp.GetRequiredService<OrderService>());
        services.DecorateOrderServiceWithCache();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();
        Assert.NotNull(cache);

        // Assert generated methods exist and are callable
        var decoratedService = provider.GetRequiredService<IOrderService>();
        Assert.NotNull(decoratedService);
        Assert.NotEqual(typeof(OrderService), decoratedService.GetType());
    }
}
