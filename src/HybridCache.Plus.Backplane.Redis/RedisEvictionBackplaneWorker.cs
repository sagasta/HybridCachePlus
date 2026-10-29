using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HybridCache.Plus.Backplane.Redis;

/// <summary>
/// Background worker that listens to Redis Pub/Sub invalidation broadcasts
/// and purges the local in-process L1 cache in real time.
/// </summary>
public sealed class RedisEvictionBackplaneWorker : BackgroundService
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly global::Microsoft.Extensions.Caching.Hybrid.HybridCache _cache;
    private readonly RedisBackplaneOptions _options;
    private readonly ILogger<RedisEvictionBackplaneWorker> _logger;
    private readonly RedisChannel _channel;
    private readonly IMemoryCache? _localCache;

    public RedisEvictionBackplaneWorker(
        IConnectionMultiplexer multiplexer,
        global::Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
        IOptions<RedisBackplaneOptions> options,
        ILogger<RedisEvictionBackplaneWorker> logger,
        IMemoryCache? memoryCache = null)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channel = RedisChannel.Literal(_options.ChannelName);

        // The default HybridCache stores its L1 entries in the DI IMemoryCache under the plain key,
        // which lets remote evictions skip the redundant L2 delete. Any other implementation falls back to RemoveAsync.
        _localCache = _options.EvictLocalCacheOnly && IsDefaultHybridCache(cache) ? memoryCache : null;
    }

    private static bool IsDefaultHybridCache(global::Microsoft.Extensions.Caching.Hybrid.HybridCache cache) =>
        cache.GetType().FullName == "Microsoft.Extensions.Caching.Hybrid.Internal.DefaultHybridCache";

    private ISubscriber? _subscriber;

    /// <summary>
    /// Subscribes before the host finishes starting, so an instance never serves traffic while deaf to evictions.
    /// (Since .NET 10, <see cref="BackgroundService"/> runs all of ExecuteAsync on the thread pool, so subscribing
    /// there would let StartAsync return before the subscription exists.) If Redis is unreachable at startup,
    /// startup proceeds and ExecuteAsync keeps retrying in the background.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Redis Eviction Backplane Worker starting. Listening on channel '{Channel}' with InstanceId '{InstanceId}'.",
            _options.ChannelName, _options.InstanceId);

        try
        {
            await SubscribeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not subscribe to Redis backplane channel '{Channel}' during startup. Retrying in the background...", _options.ChannelName);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_subscriber == null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                await SubscribeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not connect to Redis backplane channel '{Channel}'. Retrying in 1 second...", _options.ChannelName);
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown
        }
        finally
        {
            if (_subscriber != null)
            {
                try
                {
                    await _subscriber.UnsubscribeAsync(_channel).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error unsubscribing from Redis eviction channel during shutdown.");
                }
            }
        }
    }

    private async Task SubscribeAsync()
    {
        var subscriber = _multiplexer.GetSubscriber();
        await subscriber.SubscribeAsync(_channel, OnMessage).ConfigureAwait(false);
        _subscriber = subscriber;
        _logger.LogInformation("Successfully subscribed to Redis eviction backplane channel '{Channel}'.", _options.ChannelName);
    }

    private async void OnMessage(RedisChannel channel, RedisValue redisValue)
    {
        try
        {
            if (redisValue.IsNullOrEmpty) return;

            var rawBytes = (byte[]?)redisValue;
            if (rawBytes == null || rawBytes.Length == 0) return;

            var message = JsonSerializer.Deserialize(
                rawBytes.AsSpan(),
                BackplaneJsonContext.Default.BackplaneEvictionMessage);

            // Discard own echoes
            if (string.Equals(message.OriginInstanceId, _options.InstanceId, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogDebug("Received remote eviction notice: {EvictType} for '{Target}' from instance '{OriginInstanceId}'.",
                message.EvictType, message.Target, message.OriginInstanceId);

            Diagnostics.HybridCachePlusDiagnostics.RecordBackplaneReceived(message.Target);
            Diagnostics.HybridCachePlusDiagnostics.RecordEviction(message.Target, "Backplane");

            // Purge local L1 cache
            if (message.EvictType == EvictType.ByExactKey)
            {
                if (_localCache != null)
                {
                    _localCache.Remove(message.Target);
                }
                else
                {
                    await _cache.RemoveAsync(message.Target, CancellationToken.None).ConfigureAwait(false);
                }
            }
            else if (message.EvictType == EvictType.ByTag)
            {
                await _cache.RemoveByTagAsync(message.Target, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing incoming Redis eviction backplane message.");
        }
    }
}
