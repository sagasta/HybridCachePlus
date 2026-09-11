using HybridCache.Plus;
using HybridCache.Plus.Backplane;
using HybridCache.Plus.Backplane.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extension methods for registering the Redis Eviction Backplane with HybridCache.Plus.
/// </summary>
public static class RedisBackplaneServiceCollectionExtensions
{
    /// <summary>
    /// Enables real-time multi-instance cache invalidation synchronization using Redis Pub/Sub.
    /// </summary>
    public static HybridCachePlusBuilder UseRedisBackplane(
        this HybridCachePlusBuilder builder,
        Action<RedisBackplaneOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<RedisBackplaneOptions>();
        if (configure != null)
        {
            builder.Services.Configure(configure);
        }

        // Register IConnectionMultiplexer resolution if not already registered
        builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<RedisBackplaneOptions>>().Value;
            if (opts.ConnectionMultiplexer != null) return opts.ConnectionMultiplexer;
            if (opts.ConnectionMultiplexerFactory != null) return opts.ConnectionMultiplexerFactory(sp);
            if (!string.IsNullOrEmpty(opts.Configuration))
            {
                var config = ConfigurationOptions.Parse(opts.Configuration!);
                config.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(config);
            }

            throw new InvalidOperationException(
                "No Redis ConnectionMultiplexer was configured for HybridCache.Plus backplane. " +
                "Please provide ConnectionMultiplexer, ConnectionMultiplexerFactory, or Configuration in UseRedisBackplane.");
        });

        builder.Services.AddSingleton<IEvictionPublisher>(sp =>
        {
            var publisher = ActivatorUtilities.CreateInstance<RedisEvictionPublisher>(sp);
            var cache = sp.GetService<global::Microsoft.Extensions.Caching.Hybrid.HybridCache>();
            if (cache != null)
            {
                HybridCachePlusBackplaneContext.SetPublisherForCache(cache, publisher);
            }
            return publisher;
        });

        builder.Services.AddHostedService<RedisEvictionBackplaneWorker>();
        builder.Services.AddHostedService<BackplaneInitializer>();

        return builder;
    }

    private sealed class BackplaneInitializer(
        IEvictionPublisher publisher,
        global::Microsoft.Extensions.Caching.Hybrid.HybridCache? cache = null) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (cache != null)
            {
                HybridCachePlusBackplaneContext.SetPublisherForCache(cache, publisher);
            }
            else
            {
                HybridCachePlusBackplaneContext.CurrentPublisher = publisher;
            }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (ReferenceEquals(HybridCachePlusBackplaneContext.CurrentPublisher, publisher))
            {
                HybridCachePlusBackplaneContext.CurrentPublisher = null;
            }
            return Task.CompletedTask;
        }
    }
}
