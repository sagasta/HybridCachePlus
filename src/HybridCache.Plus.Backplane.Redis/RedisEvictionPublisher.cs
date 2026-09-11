using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace HybridCache.Plus.Backplane.Redis;

/// <summary>
/// Redis Pub/Sub implementation of <see cref="IEvictionPublisher"/>.
/// Broadcasts L1 cache invalidation notices to remote pods/instances in real time.
/// </summary>
public sealed class RedisEvictionPublisher : IEvictionPublisher
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly RedisBackplaneOptions _options;
    private readonly ILogger<RedisEvictionPublisher> _logger;
    private readonly RedisChannel _channel;

    public RedisEvictionPublisher(
        IConnectionMultiplexer multiplexer,
        IOptions<RedisBackplaneOptions> options,
        ILogger<RedisEvictionPublisher> logger)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channel = RedisChannel.Literal(_options.ChannelName);
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(BackplaneEvictionMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            var msg = string.IsNullOrEmpty(message.OriginInstanceId)
                ? message with { OriginInstanceId = _options.InstanceId }
                : message;

            byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(msg, BackplaneJsonContext.Default.BackplaneEvictionMessage);

            var subscriber = _multiplexer.GetSubscriber();
            await subscriber.PublishAsync(_channel, (RedisValue)utf8Bytes, CommandFlags.FireAndForget).ConfigureAwait(false);
            Diagnostics.HybridCachePlusDiagnostics.RecordBackplanePublished(msg.Target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish eviction message for target '{Target}' to Redis channel '{Channel}'.",
                message.Target, _options.ChannelName);
        }
    }
}
