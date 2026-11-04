using StackExchange.Redis;

namespace DotNetQuery.Synchronization;

/// <summary>
/// An <see cref="IQuerySyncChannel"/> over Redis pub/sub: every <see cref="QuerySynchronizer"/> subscribed to the same
/// Redis channel - typically one per server instance behind a load balancer, or one per process - receives the data
/// writes and invalidations of the others, so a mutation handled by one instance refreshes the caches of all of them.
/// </summary>
/// <remarks>
/// Redis pub/sub is fire-and-forget: an instance that is disconnected while a message is published misses it (the
/// synchronizer's last-writer-wins rule and the queries' own stale times bound the effect). Messages carry serialized
/// query data, so synchronize only what every instance may see. Dispose the channel to unsubscribe.
/// </remarks>
public sealed class RedisQuerySyncChannel : IQuerySyncChannel, IAsyncDisposable, IDisposable
{
    /// <summary>The default Redis channel name.</summary>
    public const string DefaultChannelName = "dotnetquery:sync";

    private readonly IConnectionMultiplexer _connection;
    private readonly bool _ownsConnection;
    private readonly RedisChannel _channel;
    private readonly ISubscriber _subscriber;
    private ChannelMessageQueue? _queue;
    private int _disposed;

    /// <summary>Connects to Redis with <paramref name="configuration"/> (owned and closed on dispose) and subscribes.</summary>
    public static async Task<RedisQuerySyncChannel> ConnectAsync(string configuration, string? channelName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        var connection = await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
        try
        {
            return await CreateAsync(connection, channelName, ownsConnection: true).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Subscribes over an existing <paramref name="connection"/> (left open on dispose).</summary>
    public static Task<RedisQuerySyncChannel> CreateAsync(IConnectionMultiplexer connection, string? channelName = null) =>
        CreateAsync(connection, channelName, ownsConnection: false);

    private static async Task<RedisQuerySyncChannel> CreateAsync(IConnectionMultiplexer connection, string? channelName, bool ownsConnection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var channel = new RedisQuerySyncChannel(connection, string.IsNullOrWhiteSpace(channelName) ? DefaultChannelName : channelName, ownsConnection);
        await channel.SubscribeAsync().ConfigureAwait(false);
        return channel;
    }

    private RedisQuerySyncChannel(IConnectionMultiplexer connection, string channelName, bool ownsConnection)
    {
        _connection = connection;
        _ownsConnection = ownsConnection;
        _channel = RedisChannel.Literal(channelName);
        _subscriber = connection.GetSubscriber();
    }

    /// <inheritdoc />
    public event Action<string>? MessageReceived;

    /// <inheritdoc />
    public async Task PostAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _subscriber.PublishAsync(_channel, message).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SubscribeAsync()
    {
        // A message queue delivers messages in publish order (the synchronizer relies on per-sender order).
        var queue = await _subscriber.SubscribeAsync(_channel).ConfigureAwait(false);
        queue.OnMessage(message =>
        {
            if (message.Message.HasValue && (string?)message.Message is { } text)
            {
                try
                {
                    MessageReceived?.Invoke(text);
                }
                catch (Exception)
                {
                    // A failing subscriber must not stop the subscription.
                }
            }
        });
        _queue = queue;
    }

    /// <summary>Unsubscribes (and closes the connection when this channel opened it).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_queue is { } queue)
            {
                await queue.UnsubscribeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is RedisException or ObjectDisposedException)
        {
            // Connection already gone.
        }

        if (_ownsConnection)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Unsubscribes (and closes the connection when this channel opened it).</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
