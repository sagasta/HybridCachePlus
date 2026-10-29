using Microsoft.Extensions.DependencyInjection;
using Xunit;
using HybridCache.Plus.Backplane;

namespace HybridCache.Plus.Tests.Integration;

public interface IStockRepository
{
    void SetStock(string tenantId, string sku, int quantity);
    bool TryReserve(string tenantId, string sku, out int remaining);
}

public sealed class StockRepository : IStockRepository
{
    public int Stock { get; private set; } = 10;

    public void SetStock(string tenantId, string sku, int quantity) => Stock = quantity;

    public bool TryReserve(string tenantId, string sku, out int remaining)
    {
        remaining = --Stock;
        return remaining >= 0;
    }
}

[HybridCacheKeys]
public partial interface IStockCache
{
    [CacheTemplate("tenants:{tenantId}:stock:{sku}", Tags = ["stock:{tenantId}"])]
    [InvalidatedBy<IStockRepository>(nameof(IStockRepository.SetStock), nameof(IStockRepository.TryReserve))]
    ValueTask<int> GetStockAsync(string tenantId, string sku);
}

public class SyncDecoratorTests
{
    /// <summary>
    /// Publisher whose continuation needs the captured context, like many naive implementations.
    /// </summary>
    private sealed class ContextCapturingPublisher : IEvictionPublisher
    {
        public int Published;

        public async ValueTask PublishAsync(BackplaneEvictionMessage message, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Interlocked.Increment(ref Published);
        }
    }

    /// <summary>
    /// A context that never runs posted work, like a UI thread blocked inside a synchronous call.
    /// </summary>
    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    [Fact]
    public void SyncMutation_UnderBlockingSynchronizationContext_InvalidatesWithoutDeadlock()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        var publisher = new ContextCapturingPublisher();
        var decorator = new StockRepositoryCacheDecorator(new StockRepository(), cache, publisher);

        Exception? failure = null;
        var reserved = false;
        var remaining = -1;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                decorator.SetStock("t1", "sku-1", 5);
                reserved = decorator.TryReserve("t1", "sku-1", out remaining);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }) { IsBackground = true };

        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Synchronous decorated call deadlocked on the captured SynchronizationContext.");
        Assert.Null(failure);
        Assert.True(reserved);
        Assert.Equal(4, remaining);
        Assert.Equal(4, publisher.Published); // key + tag for each of the two mutations
    }
}
