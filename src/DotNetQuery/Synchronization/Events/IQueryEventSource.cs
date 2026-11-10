namespace DotNetQuery.Synchronization;

/// <summary>The connection state of an <see cref="IQueryEventSource"/>.</summary>
public enum QueryEventSourceState
{
    /// <summary>Not connected, and not trying to: never started, stopped, or permanently failed (see the change's error).</summary>
    Disconnected,

    /// <summary>Establishing the initial connection.</summary>
    Connecting,

    /// <summary>Connected, and every subscription (handlers, server-side groups) is in place: events are delivered.</summary>
    Connected,

    /// <summary>The connection was lost and the transport is trying to restore it. Events published meanwhile are missed.</summary>
    Reconnecting,

    /// <summary>The connection is back and the source is restoring server-side subscriptions (for example rejoining groups).</summary>
    Resubscribing,
}

/// <summary>A state transition reported by <see cref="IQueryEventSource.StateChanged"/>.</summary>
/// <param name="State">The new state.</param>
/// <param name="Error">The error that caused the transition, if any (a lost connection, a failed start).</param>
public readonly record struct QueryEventSourceStateChange(QueryEventSourceState State, Exception? Error = null);

/// <summary>
/// A transport delivering named, typed events to a <see cref="QueryEventSynchronizer"/> - a SignalR hub connection
/// (<c>Msynk.DotNetQuery.SignalR</c>), a message broker, server-sent events, or <see cref="InMemoryQueryEventSource"/> in
/// tests. The core takes no dependency on any transport.
/// </summary>
/// <remarks>
/// <para>
/// A source must report <see cref="QueryEventSourceState.Connected"/> only once events published from then on are
/// delivered - after restoring server-side state such as group memberships, which do not survive a reconnect. The
/// synchronizer runs its recovery after every transition to <see cref="QueryEventSourceState.Connected"/> that follows
/// a lost connection, because events published while disconnected are missed.
/// </para>
/// <para>
/// Handlers should be invoked one event at a time in the order the transport received them. A handler returns as soon as
/// the event is queued (or, with <see cref="QueueOverflowPolicy.Wait"/>, once there is room in the queue), never after
/// the cache work it causes.
/// </para>
/// </remarks>
public interface IQueryEventSource
{
    /// <summary>Gets the current connection state.</summary>
    QueryEventSourceState State { get; }

    /// <summary>Raised on every state transition.</summary>
    event Action<QueryEventSourceStateChange>? StateChanged;

    /// <summary>
    /// Starts delivering events named <paramref name="eventName"/> with payload <typeparamref name="TEvent"/> to
    /// <paramref name="handler"/>. Dispose the result to stop. Subscriptions survive reconnects.
    /// </summary>
    IDisposable Subscribe<TEvent>(string eventName, Func<TEvent, ValueTask> handler);

    /// <summary>Connects (if not already connected) and completes once the source is <see cref="QueryEventSourceState.Connected"/>.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// An in-process <see cref="IQueryEventSource"/> for tests, demos and in-process publishers: <see cref="PublishAsync"/>
/// delivers an event to its subscribers while connected (and drops it otherwise, like a real transport that is offline),
/// and <see cref="SetState"/> simulates connection loss and recovery.
/// </summary>
public sealed class InMemoryQueryEventSource : IQueryEventSource
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Subscription>> _subscriptions = new(StringComparer.Ordinal);
    private QueryEventSourceState _state;

    /// <inheritdoc />
    public QueryEventSourceState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <inheritdoc />
    public event Action<QueryEventSourceStateChange>? StateChanged;

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(string eventName, Func<TEvent, ValueTask> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, eventName, typeof(TEvent), payload => handler((TEvent)payload!));
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(eventName, out var list))
            {
                _subscriptions[eventName] = list = [];
            }

            list.Add(subscription);
        }

        return subscription;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (State is not QueryEventSourceState.Connected)
        {
            SetState(QueryEventSourceState.Connected);
        }

        return Task.CompletedTask;
    }

    /// <summary>Gets how many handlers are subscribed to <paramref name="eventName"/>.</summary>
    public int SubscriberCount(string eventName)
    {
        lock (_gate)
        {
            return _subscriptions.TryGetValue(eventName, out var list) ? list.Count : 0;
        }
    }

    /// <summary>Moves the source to <paramref name="state"/> and raises <see cref="StateChanged"/>.</summary>
    public void SetState(QueryEventSourceState state, Exception? error = null)
    {
        lock (_gate)
        {
            _state = state;
        }

        StateChanged?.Invoke(new QueryEventSourceStateChange(state, error));
    }

    /// <summary>
    /// Delivers an event to every subscriber of <paramref name="eventName"/>, one after another. Returns
    /// <see langword="false"/> (delivering nothing) unless the source is <see cref="QueryEventSourceState.Connected"/> or
    /// <see cref="QueryEventSourceState.Resubscribing"/> - an event published while disconnected is missed.
    /// </summary>
    public async ValueTask<bool> PublishAsync<TEvent>(string eventName, TEvent payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        Subscription[] targets;
        lock (_gate)
        {
            if (_state is not (QueryEventSourceState.Connected or QueryEventSourceState.Resubscribing)
                || !_subscriptions.TryGetValue(eventName, out var list))
            {
                return false;
            }

            targets = [.. list];
        }

        foreach (var target in targets)
        {
            if (!target.PayloadType.IsInstanceOfType(payload) && payload is not null)
            {
                throw new InvalidOperationException(
                    $"Event '{eventName}' is subscribed with payload type {target.PayloadType.Name}, not {typeof(TEvent).Name}.");
            }

            await target.Handler(payload).ConfigureAwait(false);
        }

        return true;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(subscription.EventName, out var list) && list.Remove(subscription) && list.Count == 0)
            {
                _subscriptions.Remove(subscription.EventName);
            }
        }
    }

    private sealed class Subscription(InMemoryQueryEventSource owner, string eventName, Type payloadType, Func<object?, ValueTask> handler) : IDisposable
    {
        private int _disposed;

        public string EventName { get; } = eventName;

        public Type PayloadType { get; } = payloadType;

        public Func<object?, ValueTask> Handler { get; } = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(this);
            }
        }
    }
}
