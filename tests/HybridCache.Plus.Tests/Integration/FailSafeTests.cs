using System.Diagnostics.Metrics;
using HybridCache.Plus.Diagnostics;
using HybridCache.Plus.Options;
using HybridCache.Plus.Policies;
using HybridCache.Plus.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HybridCache.Plus.Tests.Integration;

public record PriceDto(string TenantId, string Sku, decimal Amount);

public interface IPriceRepository
{
    Task SetPriceAsync(string tenantId, string sku, decimal amount, CancellationToken cancellationToken = default);
    void SetPrice(string tenantId, string sku, decimal amount);
}

public sealed class PriceRepository : IPriceRepository
{
    public Task SetPriceAsync(string tenantId, string sku, decimal amount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void SetPrice(string tenantId, string sku, decimal amount) { }
}

[HybridCacheKeys]
public partial interface IPriceCache
{
    [CacheTemplate("tenants:{tenantId}:prices:{sku}",
        PolicyName = "FailSafePrices",
        LocalTtlSeconds = 60,
        DistributedTtlSeconds = 600,
        Tags = ["prices:{tenantId}"],
        FailSafeMaxAgeSeconds = 3600)]
    [InvalidatedBy<IPriceRepository>(nameof(IPriceRepository.SetPriceAsync), nameof(IPriceRepository.SetPrice))]
    ValueTask<PriceDto> GetPriceAsync(string tenantId, string sku);

    [CacheTemplate("tenants:{tenantId}:slow-prices:{sku}",
        PolicyName = "FailSafeSlowPrices",
        FailSafeMaxAgeSeconds = 3600,
        FactorySoftTimeoutMs = 100)]
    ValueTask<PriceDto> GetSlowPriceAsync(string tenantId, string sku);

    [CacheTemplate("tenants:{tenantId}:plain-prices:{sku}", PolicyName = "PlainPrices")]
    ValueTask<PriceDto> GetPlainPriceAsync(string tenantId, string sku);
}

public class FailSafeTests
{
    private static (ServiceProvider Provider, Microsoft.Extensions.Caching.Hybrid.HybridCache Cache, IPriceRepository Repo) CreateEnvironment()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        services.AddSingleton<IPriceRepository, PriceRepository>();
        services.DecoratePriceRepositoryWithCache();

        var provider = services.BuildServiceProvider();
        return (provider,
            provider.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(),
            provider.GetRequiredService<IPriceRepository>());
    }

    private static ValueTask<PriceDto> Fresh(decimal amount) => ValueTask.FromResult(new PriceDto("t1", "sku", amount));

    private static ValueTask<PriceDto> Failing(CancellationToken _) =>
        ValueTask.FromException<PriceDto>(new InvalidOperationException("database down"));

    /// <summary>
    /// Drops only the primary entry, as its expiration would, leaving the fail-safe backup in place.
    /// </summary>
    private static ValueTask ExpirePrimaryAsync(Microsoft.Extensions.Caching.Hybrid.HybridCache cache, string key) =>
        cache.RemoveAsync(key);

    [Fact]
    public async Task FactoryFailure_AfterExpiration_ServesLastKnownValue()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;

        await cache.GetPriceAsync("t1", "sku", _ => Fresh(10m));
        await ExpirePrimaryAsync(cache, PriceCacheHybridCacheExtensions.FormatPriceKey("t1", "sku"));

        var stale = await cache.GetPriceAsync("t1", "sku", Failing);

        Assert.Equal(10m, stale.Amount);
    }

    [Fact]
    public async Task FactoryFailure_WithoutBackup_PropagatesException()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetPriceAsync("t1", "never-loaded", Failing));
        Assert.Equal("database down", ex.Message);
    }

    [Fact]
    public async Task FactoryFailure_ThrottlesRetries_WhileServingBackup()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;

        await cache.GetPriceAsync("t1", "sku", _ => Fresh(10m));
        await ExpirePrimaryAsync(cache, PriceCacheHybridCacheExtensions.FormatPriceKey("t1", "sku"));

        var failures = 0;
        ValueTask<PriceDto> CountingFailure(CancellationToken ct)
        {
            failures++;
            return Failing(ct);
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(10m, (await cache.GetPriceAsync("t1", "sku", CountingFailure)).Amount);
        }

        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task SuccessfulReload_RefreshesBackup()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;
        var key = PriceCacheHybridCacheExtensions.FormatPriceKey("t1", "sku");

        await cache.GetPriceAsync("t1", "sku", _ => Fresh(10m));
        await ExpirePrimaryAsync(cache, key);
        await cache.GetPriceAsync("t1", "sku", _ => Fresh(20m));
        await ExpirePrimaryAsync(cache, key);

        Assert.Equal(20m, (await cache.GetPriceAsync("t1", "sku", Failing)).Amount);
    }

    [Fact]
    public async Task AsyncDecoratorMutation_RemovesBackup()
    {
        var (provider, cache, repo) = CreateEnvironment();
        using var _ = provider;

        await cache.GetPriceAsync("t1", "sku", _ => Fresh(10m));
        await repo.SetPriceAsync("t1", "sku", 15m);

        // The old price is known to be wrong now: it must not come back through fail-safe.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetPriceAsync("t1", "sku", Failing));
    }

    [Fact]
    public async Task SyncDecoratorMutation_RemovesBackup()
    {
        var (provider, cache, repo) = CreateEnvironment();
        using var _ = provider;

        await cache.GetPriceAsync("t1", "sku", _ => Fresh(10m));
        repo.SetPrice("t1", "sku", 15m);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetPriceAsync("t1", "sku", Failing));
    }

    [Fact]
    public async Task GeneratedEvict_RemovesBackup()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;

        await cache.GetPriceAsync("t1", "sku", _ => Fresh(10m));
        await cache.EvictPriceAsync("t1", "sku");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetPriceAsync("t1", "sku", Failing));
    }

    [Fact]
    public async Task TagEviction_RemovesBackup()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;

        await cache.GetPriceAsync("t1", "sku", _ => Fresh(10m));
        await cache.EvictPriceByTenantIdTagAsync("t1");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetPriceAsync("t1", "sku", Failing));
    }

    [Fact]
    public async Task SlowFactory_PastSoftTimeout_ServesBackupAndRefreshesInBackground()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;
        var key = PriceCacheHybridCacheExtensions.FormatSlowPriceKey("t1", "sku");

        await cache.GetSlowPriceAsync("t1", "sku", _ => Fresh(10m));
        await ExpirePrimaryAsync(cache, key);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<PriceDto> SlowFactory(CancellationToken ct)
        {
            await release.Task.WaitAsync(ct);
            refreshed.TrySetResult();
            return new PriceDto("t1", "sku", 30m);
        }

        var stale = await cache.GetSlowPriceAsync("t1", "sku", SlowFactory).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10m, stale.Amount);

        release.SetResult();
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The background refresh lands in the cache: the next read is a hit with the new value.
        PriceDto fresh = default!;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            fresh = await cache.GetSlowPriceAsync("t1", "sku", Failing);
            if (fresh.Amount == 30m) break;
            await Task.Delay(20);
        }
        Assert.Equal(30m, fresh.Amount);
    }

    [Fact]
    public async Task SlowFactory_WithoutBackup_WaitsForFactory()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;

        async ValueTask<PriceDto> SlowFactory(CancellationToken ct)
        {
            await Task.Delay(300, ct);
            return new PriceDto("t1", "sku", 30m);
        }

        var value = await cache.GetSlowPriceAsync("t1", "new-sku", SlowFactory);
        Assert.Equal(30m, value.Amount);
    }

    [Fact]
    public async Task CallerCancellation_IsNotMaskedByBackup()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;

        await cache.GetPriceAsync("t1", "sku", _ => Fresh(10m));
        await ExpirePrimaryAsync(cache, PriceCacheHybridCacheExtensions.FormatPriceKey("t1", "sku"));

        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<PriceDto> HangingFactory(CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new PriceDto("t1", "sku", 99m);
        }

        var call = cache.GetPriceAsync("t1", "sku", HangingFactory, cancellationToken: cts.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MethodWithoutFailSafe_PropagatesException()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;
        var key = PriceCacheHybridCacheExtensions.FormatPlainPriceKey("t1", "sku");

        await cache.GetPlainPriceAsync("t1", "sku", _ => Fresh(10m));
        await ExpirePrimaryAsync(cache, key);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetPlainPriceAsync("t1", "sku", Failing));
        Assert.Equal(0, await CountBackupAsync(cache, key));
    }

    [Fact]
    public async Task PolicyConfiguration_CanDisableFailSafe()
    {
        var (provider, cache, _) = CreateEnvironment();
        using var _ = provider;

        var options = new HybridCachePlusPolicyOptions();
        options.Policies["FailSafePrices"] = new CachePolicyOptions { FailSafeMaxAgeSeconds = 0 };
        HybridCachePlusPolicyRegistry.Register(cache, new HybridCachePlusPolicyRegistry(options));

        await cache.GetPriceAsync("t1", "sku", _ => Fresh(10m));
        await ExpirePrimaryAsync(cache, PriceCacheHybridCacheExtensions.FormatPriceKey("t1", "sku"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetPriceAsync("t1", "sku", Failing));
    }

    private static async Task<int> CountBackupAsync(Microsoft.Extensions.Caching.Hybrid.HybridCache cache, string key)
    {
        var created = 0;
        await cache.GetOrCreateAsync<int>(
            HybridCachePlusFailSafe.GetBackupKey(key),
            _ => { created++; return ValueTask.FromResult(0); });
        return created == 1 ? 0 : 1;
    }
}

/// <summary>
/// Shares the collection of the tests that toggle <see cref="HybridCachePlusDiagnostics.IsEnabled"/> globally.
/// </summary>
[Collection("StaticStateTests")]
public class FailSafeDiagnosticsTests
{
    [Fact]
    public async Task FailSafeActivation_IsRecordedAsMetric()
    {
        HybridCachePlusDiagnostics.IsEnabled = true;
        var services = new ServiceCollection();
        services.AddHybridCache();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        var reasons = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == HybridCachePlusDiagnostics.MeterName && instrument.Name == "hybridcache_plus.failsafe.activations")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "failsafe.reason" && tag.Value is string reason)
                {
                    lock (reasons) reasons.Add(reason);
                }
            }
        });
        listener.Start();

        await cache.GetPriceAsync("t1", "metric-sku", _ => ValueTask.FromResult(new PriceDto("t1", "metric-sku", 10m)));
        await cache.RemoveAsync(PriceCacheHybridCacheExtensions.FormatPriceKey("t1", "metric-sku"));
        await cache.GetPriceAsync("t1", "metric-sku", _ => ValueTask.FromException<PriceDto>(new InvalidOperationException("database down")));

        lock (reasons) Assert.Contains("FactoryError", reasons);
    }
}
