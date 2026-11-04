using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.SignalR.Client;

namespace DotNetQuery.SignalR;

/// <summary>Options for a <see cref="HubConnectionQueryEventSource"/>.</summary>
public sealed record HubConnectionQueryEventSourceOptions
{
    /// <summary>
    /// Whether the source owns the connection: disposing the source then disposes (stops) the connection. Defaults to
    /// <see langword="false"/> - a connection the application shares with other features is never stopped by the source.
    /// </summary>
    public bool OwnsConnection { get; init; }

    /// <summary>
    /// Restores server-side subscriptions - for example joining the groups of the current tenant - each time the
    /// connection is established: on start and after every reconnect (server group memberships do not survive a new
    /// connection). Runs before the source reports <see cref="QueryEventSourceState.Connected"/>, so the recovery that
    /// follows sees every event published after it. Must be idempotent.
    /// </summary>
    /// <example><code>OnConnected = (hub, ct) => hub.InvokeAsync("JoinTenant", tenantId, ct)</code></example>
    public Func<HubConnection, CancellationToken, Task>? OnConnected { get; init; }

    /// <summary>
    /// Whether, and after how long, to try again when starting fails, when <see cref="OnConnected"/> fails, or when the
    /// connection closes with an error (for example after automatic reconnection gave up): receives the zero-based
    /// attempt number and the error, and returns the delay before the next attempt or <see langword="null"/> to give up
    /// (the source then reports <see cref="QueryEventSourceState.Disconnected"/> with the error). Defaults to
    /// <see langword="null"/>: no retries. Return <see langword="null"/> for errors that retrying cannot fix, such as an
    /// authorization failure.
    /// </summary>
    public Func<int, Exception, TimeSpan?>? RestartDelay { get; init; }
}

/// <summary>
/// An <see cref="IQueryEventSource"/> over a SignalR <see cref="HubConnection"/>: each subscribed event name becomes a
/// typed hub handler (<c>connection.On&lt;TEvent&gt;</c>), and the connection's lifecycle - start, reconnecting,
/// reconnected, closed - is reported to the <see cref="QueryEventSynchronizer"/>, which reconciles the cache after every
/// reconnect.
/// </summary>
/// <remarks>
/// <para>
/// Hub methods must send exactly one argument (the event payload). Configure automatic reconnection on the connection
/// (<c>WithAutomaticReconnect()</c>); it covers lost connections, while <see cref="HubConnectionQueryEventSourceOptions.RestartDelay"/>
/// covers failed starts and connections closed for good. Access tokens are renewed through the connection's
/// <c>AccessTokenProvider</c>, which SignalR calls on every (re)connect.
/// </para>
/// <para>
/// Handlers stay registered across reconnects. Disposing the source removes them and detaches from the connection; the
/// connection itself is disposed only with <see cref="HubConnectionQueryEventSourceOptions.OwnsConnection"/>.
/// </para>
/// </remarks>
public sealed class HubConnectionQueryEventSource : IQueryEventSource, IAsyncDisposable
{
    private readonly HubConnectionQueryEventSourceOptions _options;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private QueryEventSourceState _state;
    private int _disposed;
    private int _restarting;

    /// <summary>Creates a source over <paramref name="connection"/>.</summary>
    public HubConnectionQueryEventSource(HubConnection connection, HubConnectionQueryEventSourceOptions? options = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? new HubConnectionQueryEventSourceOptions();
        connection.Reconnecting += OnReconnectingAsync;
        connection.Reconnected += OnReconnectedAsync;
        connection.Closed += OnClosedAsync;
        _state = connection.State switch
        {
            HubConnectionState.Connected => QueryEventSourceState.Connected,
            HubConnectionState.Connecting => QueryEventSourceState.Connecting,
            HubConnectionState.Reconnecting => QueryEventSourceState.Reconnecting,
            _ => QueryEventSourceState.Disconnected,
        };
    }

    /// <summary>Gets the hub connection.</summary>
    public HubConnection Connection { get; }

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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var registration = Connection.On<TEvent>(eventName, payload => handler(payload).AsTask());
        var subscription = new Subscription(this, registration);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    /// <summary>
    /// Starts the connection if it is disconnected (retrying per <see cref="HubConnectionQueryEventSourceOptions.RestartDelay"/>),
    /// runs <see cref="HubConnectionQueryEventSourceOptions.OnConnected"/>, and completes once events flow. A connection
    /// already connected by the application is only resubscribed.
    /// </summary>
    /// <exception cref="Exception">The connection could not be started and no (further) retry was allowed.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await StartCoreAsync(linked.Token, throwOnFailure: true).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken, bool throwOnFailure)
    {
        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (Connection.State is HubConnectionState.Disconnected)
                    {
                        SetState(QueryEventSourceState.Connecting, null);
                        await Connection.StartAsync(cancellationToken).ConfigureAwait(false);
                    }

                    // Started by the application concurrently: SignalR raises no event when an initial start completes.
                    while (Connection.State is HubConnectionState.Connecting)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                    }

                    if (Connection.State is HubConnectionState.Connected)
                    {
                        await ResubscribeAsync(cancellationToken).ConfigureAwait(false);
                    }

                    // Otherwise the connection is (re)connecting under SignalR's control; its Reconnected event resumes.
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    SetState(QueryEventSourceState.Disconnected, null);
                    throw;
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _disposed) != 0 || _options.RestartDelay?.Invoke(attempt, ex) is not { } delay)
                    {
                        SetState(QueryEventSourceState.Disconnected, ex);
                        if (throwOnFailure)
                        {
                            throw;
                        }

                        return;
                    }

                    SetState(QueryEventSourceState.Connecting, ex);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ResubscribeAsync(CancellationToken cancellationToken)
    {
        if (_options.OnConnected is { } onConnected)
        {
            SetState(QueryEventSourceState.Resubscribing, null);
            await onConnected(Connection, cancellationToken).ConfigureAwait(false);
        }

        // The connection may have dropped while resubscribing; its own events report that.
        if (Connection.State is HubConnectionState.Connected)
        {
            SetState(QueryEventSourceState.Connected, null);
        }
    }

    private Task OnReconnectingAsync(Exception? error)
    {
        SetState(QueryEventSourceState.Reconnecting, error);
        return Task.CompletedTask;
    }

    private Task OnReconnectedAsync(string? connectionId)
    {
        // Off SignalR's event dispatch: resubscribing invokes hub methods and may wait between retries.
        _ = Task.Run(() => StartCoreAsync(_lifetime.Token, throwOnFailure: false));
        return Task.CompletedTask;
    }

    private Task OnClosedAsync(Exception? error)
    {
        SetState(QueryEventSourceState.Disconnected, error);

        // A close without an error is a deliberate stop; a close with one (reconnection gave up, the server dropped the
        // connection) is restarted when a restart policy allows it.
        if (error is not null && _options.RestartDelay is not null && Volatile.Read(ref _disposed) == 0
            && Interlocked.Exchange(ref _restarting, 1) == 0)
        {
            _ = Task.Run(() => RestartAsync(error));
        }

        return Task.CompletedTask;
    }

    private async Task RestartAsync(Exception error)
    {
        try
        {
            var token = _lifetime.Token;
            for (var attempt = 0; !token.IsCancellationRequested; attempt++)
            {
                if (_options.RestartDelay!(attempt, error) is not { } delay)
                {
                    return;
                }

                await Task.Delay(delay, token).ConfigureAwait(false);
                if (Connection.State is not HubConnectionState.Disconnected)
                {
                    return; // restarted by the application meanwhile
                }

                try
                {
                    await StartCoreAsync(token, throwOnFailure: true).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        finally
        {
            Volatile.Write(ref _restarting, 0);
        }
    }

    private void SetState(QueryEventSourceState state, Exception? error)
    {
        lock (_gate)
        {
            if (_state == state && error is null)
            {
                return;
            }

            _state = state;
        }

        try
        {
            StateChanged?.Invoke(new QueryEventSourceStateChange(state, error));
        }
        catch (Exception)
        {
            // Subscribers must not break the connection's event dispatch.
        }
    }

    /// <summary>
    /// Removes the hub handlers, detaches from the connection's lifecycle events, stops pending restarts and - only with
    /// <see cref="HubConnectionQueryEventSourceOptions.OwnsConnection"/> - disposes the connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        Connection.Reconnecting -= OnReconnectingAsync;
        Connection.Reconnected -= OnReconnectedAsync;
        Connection.Closed -= OnClosedAsync;

        Subscription[] subscriptions;
        lock (_gate)
        {
            subscriptions = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Release();
        }

        if (_options.OwnsConnection)
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            _state = QueryEventSourceState.Disconnected;
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription(HubConnectionQueryEventSource owner, IDisposable registration) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            owner.Remove(this);
            Release();
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                registration.Dispose();
            }
        }
    }
}
