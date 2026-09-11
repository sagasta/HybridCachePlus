using NSubstitute;
using StackExchange.Redis;

namespace HybridCache.Plus.Tests.Backplane;

/// <summary>
/// In-memory Redis Pub/Sub broker for simulating real-time multi-instance distributed communication.
/// </summary>
public sealed class InMemoryRedisBroker
{
    private readonly List<Action<RedisChannel, RedisValue>> _subscribers = [];
    private readonly object _lock = new();

    public int SubscriberCount
    {
        get
        {
            lock (_lock) return _subscribers.Count;
        }
    }

    public void Subscribe(Action<RedisChannel, RedisValue> handler)
    {
        lock (_lock)
        {
            _subscribers.Add(handler);
        }
    }

    public void Unsubscribe(Action<RedisChannel, RedisValue> handler)
    {
        lock (_lock)
        {
            _subscribers.Remove(handler);
        }
    }

    public Task<long> PublishAsync(RedisChannel channel, RedisValue message)
    {
        List<Action<RedisChannel, RedisValue>> handlers;
        lock (_lock)
        {
            handlers = [.. _subscribers];
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(channel, message);
            }
            catch
            {
                // Ignore handler exceptions in broadcast
            }
        }

        return Task.FromResult((long)handlers.Count);
    }

    public IConnectionMultiplexer CreateMultiplexer()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var sub = Substitute.For<ISubscriber>();

        sub.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var channel = callInfo.Arg<RedisChannel>();
                var value = callInfo.Arg<RedisValue>();
                return PublishAsync(channel, value);
            });

        sub.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var handler = callInfo.Arg<Action<RedisChannel, RedisValue>>();
                Subscribe(handler);
                return Task.CompletedTask;
            });

        sub.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var handler = callInfo.Arg<Action<RedisChannel, RedisValue>>();
                if (handler != null) Unsubscribe(handler);
                return Task.CompletedTask;
            });

        mux.GetSubscriber(Arg.Any<object?>()).Returns(sub);
        mux.GetSubscriber().Returns(sub);
        return mux;
    }
}
