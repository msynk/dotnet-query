using Microsoft.JSInterop;

namespace DotNetQuery.Blazor;

/// <summary>
/// Bridges browser events to DotNetQuery's signal managers. Loads a small JS module that listens for
/// <c>focus</c>, <c>online</c>/<c>offline</c>, and <c>visibilitychange</c> and invokes the
/// <c>[JSInvokable]</c> callbacks below, which raise the corresponding managers.
/// </summary>
/// <remarks>
/// Registered as a scoped service by <c>AddDotNetQueryBlazor</c> (one per circuit / browser tab) and
/// started by placing <c>&lt;DotNetQueryBrowserSignals /&gt;</c> once in an interactive layout.
/// </remarks>
public sealed class BrowserSignalService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly FocusManager _focus;
    private readonly OnlineManager _online;
    private readonly ResumeManager _resume;
    private readonly ConnectivityProvider _connectivity;
    private DotNetObjectReference<BrowserSignalService>? _selfRef;
    private IJSObjectReference? _module;
    private IJSObjectReference? _registration;
    private readonly object _sync = new();
    private int _initialized;
    private int _disposed;

    /// <summary>Creates the bridge from the browser to the DotNetQuery managers.</summary>
    public BrowserSignalService(
        IJSRuntime js,
        FocusManager focus,
        OnlineManager online,
        ResumeManager resume,
        ConnectivityProvider connectivity)
    {
        _js = js;
        _focus = focus;
        _online = online;
        _resume = resume;
        _connectivity = connectivity;
    }

    /// <summary>
    /// Loads the JS module and registers the browser event listeners. Call once after first render.
    /// Safe to call multiple times; subsequent calls are no-ops.
    /// </summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var selfRef = DotNetObjectReference.Create(this);
        IJSObjectReference? module = null;
        IJSObjectReference? registration = null;
        try
        {
            module = await _js.InvokeAsync<IJSObjectReference>(
                "import",
                cancellationToken,
                "./_content/DotNetQuery.Blazor/dotnetquery.js");
            if (Volatile.Read(ref _disposed) == 0)
            {
                registration = await module.InvokeAsync<IJSObjectReference>("register", cancellationToken, selfRef);
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
        {
            // The circuit went away (or we were disposed) while starting up; nothing to wire.
        }

        lock (_sync)
        {
            if (_disposed == 0)
            {
                _selfRef = selfRef;
                _module = module;
                _registration = registration;
                return;
            }
        }

        // Disposed while initializing: release what was created, since DisposeAsync could not see it.
        await ReleaseAsync(module, registration, selfRef).ConfigureAwait(false);
    }

    /// <summary>Invoked from JS when the window regains focus.</summary>
    [JSInvokable]
    public void OnFocus() => _focus.Signal();

    /// <summary>Invoked from JS when the document becomes visible again (app resume).</summary>
    [JSInvokable]
    public void OnResume() => _resume.Signal();

    /// <summary>Invoked from JS with the current connectivity, initially and on every change.</summary>
    [JSInvokable]
    public void OnConnectivityChanged(bool online)
    {
        var wasOnline = _connectivity.IsOnline;
        _connectivity.SetOnline(online);

        // Only an offline → online transition is a reconnect; the initial report must not trigger a
        // refetch storm right after startup.
        if (online && !wasOnline)
        {
            _online.Signal();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IJSObjectReference? module;
        IJSObjectReference? registration;
        DotNetObjectReference<BrowserSignalService>? selfRef;
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            (module, registration, selfRef) = (_module, _registration, _selfRef);
            (_module, _registration, _selfRef) = (null, null, null);
        }

        await ReleaseAsync(module, registration, selfRef).ConfigureAwait(false);
    }

    private static async Task ReleaseAsync(
        IJSObjectReference? module,
        IJSObjectReference? registration,
        DotNetObjectReference<BrowserSignalService>? selfRef)
    {
        try
        {
            if (registration is not null)
            {
                await registration.InvokeVoidAsync("dispose");
                await registration.DisposeAsync();
            }

            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException or InvalidOperationException)
        {
            // Circuit already gone (Blazor Server disconnect) or JS unavailable; nothing to clean up.
        }

        selfRef?.Dispose();
    }
}
