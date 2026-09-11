using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using HybridCache.Plus.Backplane;

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

    public RedisEvictionBackplaneWorker(
        IConnectionMultiplexer multiplexer,
        global::Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
        IOptions<RedisBackplaneOptions> options,
        ILogger<RedisEvictionBackplaneWorker> logger)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channel = RedisChannel.Literal(_options.ChannelName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Redis Eviction Backplane Worker started. Listening on channel '{Channel}' with InstanceId '{InstanceId}'.",
            _options.ChannelName, _options.InstanceId);

        var subscriber = _multiplexer.GetSubscriber();

        await subscriber.SubscribeAsync(_channel, async (_, redisValue) =>
        {
            try
            {
                if (redisValue.IsNullOrEmpty) return;

                byte[]? rawBytes = (byte[]?)redisValue;
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

                // Purge local L1 cache
                if (message.EvictType == EvictType.ByExactKey)
                {
                    await _cache.RemoveAsync(message.Target, CancellationToken.None).ConfigureAwait(false);
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
        }).ConfigureAwait(false);

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
            try
            {
                await subscriber.UnsubscribeAsync(_channel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error unsubscribing from Redis eviction channel during shutdown.");
            }
        }
    }
}
