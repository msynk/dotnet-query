using DotNetQuery.Synchronization;
using Microsoft.JSInterop;

namespace DotNetQuery.Blazor;

/// <summary>
/// An <see cref="IQuerySyncChannel"/> over the browser's <c>BroadcastChannel</c> API, connecting the DotNetQuery caches
/// of every tab of the same origin. Registered by <c>AddCrossTabSync</c> and started by
/// <c>&lt;DotNetQueryCrossTabSync /&gt;</c> once the page is interactive; messages posted before that (for example
/// during prerendering, where there is no browser) are dropped.
/// </summary>
public sealed class BroadcastChannelQuerySync : IQuerySyncChannel, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly string _name;
    private readonly object _sync = new();
    private DotNetObjectReference<BroadcastChannelQuerySync>? _selfRef;
    private IJSObjectReference? _module;
    private IJSObjectReference? _handle;
    private int _started;
    private int _disposed;

    /// <summary>Creates the transport for the BroadcastChannel named <paramref name="name"/>.</summary>
    public BroadcastChannelQuerySync(IJSRuntime js, string name)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
        _name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A channel name is required.", nameof(name)) : name;
    }

    /// <inheritdoc />
    public event Action<string>? MessageReceived;

    /// <summary>Gets a value indicating whether the browser channel is open.</summary>
    public bool IsConnected
    {
        get { lock (_sync) { return _handle is not null; } }
    }

    /// <summary>Opens the browser channel. Call after the first interactive render; later calls are no-ops.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var selfRef = DotNetObjectReference.Create(this);
        IJSObjectReference? module = null;
        IJSObjectReference? handle = null;
        try
        {
            module = await _js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "./_content/DotNetQuery.Blazor/dotnetquery-sync.js");
            handle = await module.InvokeAsync<IJSObjectReference?>("open", cancellationToken, _name, selfRef);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException or InvalidOperationException)
        {
            // No browser (prerendering) or the circuit went away while starting.
        }

        lock (_sync)
        {
            if (_disposed == 0)
            {
                (_selfRef, _module, _handle) = (selfRef, module, handle);
                return;
            }
        }

        await ReleaseAsync(module, handle, selfRef).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PostAsync(string message, CancellationToken cancellationToken = default)
    {
        IJSObjectReference? handle;
        lock (_sync)
        {
            handle = _handle;
        }

        if (handle is null)
        {
            return;
        }

        try
        {
            await handle.InvokeVoidAsync("post", cancellationToken, message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
        {
            // The circuit is gone; nothing to send to.
        }
    }

    /// <summary>Invoked from JS for each message another tab posted.</summary>
    [JSInvokable]
    public void OnMessage(string message) => MessageReceived?.Invoke(message);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IJSObjectReference? module;
        IJSObjectReference? handle;
        DotNetObjectReference<BroadcastChannelQuerySync>? selfRef;
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            (module, handle, selfRef) = (_module, _handle, _selfRef);
            (_module, _handle, _selfRef) = (null, null, null);
        }

        await ReleaseAsync(module, handle, selfRef).ConfigureAwait(false);
    }

    private static async Task ReleaseAsync(IJSObjectReference? module, IJSObjectReference? handle, DotNetObjectReference<BroadcastChannelQuerySync>? selfRef)
    {
        try
        {
            if (handle is not null)
            {
                await handle.InvokeVoidAsync("dispose");
                await handle.DisposeAsync();
            }

            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException or InvalidOperationException)
        {
            // Circuit already gone; nothing to clean up.
        }

        selfRef?.Dispose();
    }
}
