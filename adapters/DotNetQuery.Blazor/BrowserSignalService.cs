using Microsoft.JSInterop;

namespace DotNetQuery.Blazor;

/// <summary>
/// Bridges browser events to DotNetQuery's signal managers. Loads a small JS module that listens for
/// <c>focus</c>, <c>online</c>/<c>offline</c>, and <c>visibilitychange</c> and invokes the
/// <c>[JSInvokable]</c> callbacks below, which raise the corresponding managers.
/// </summary>
public sealed class BrowserSignalService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly FocusManager _focus;
    private readonly OnlineManager _online;
    private readonly ResumeManager _resume;
    private readonly ConnectivityProvider _connectivity;
    private DotNetObjectReference<BrowserSignalService>? _selfRef;
    private IJSObjectReference? _module;
    private bool _initialized;

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
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _selfRef = DotNetObjectReference.Create(this);
        _module = await _js.InvokeAsync<IJSObjectReference>(
            "import",
            cancellationToken,
            "./_content/DotNetQuery.Blazor/dotnetquery.js");
        await _module.InvokeVoidAsync("register", cancellationToken, _selfRef);
    }

    /// <summary>Invoked from JS when the window regains focus.</summary>
    [JSInvokable]
    public void OnFocus() => _focus.Signal();

    /// <summary>Invoked from JS when the document becomes visible again (app resume).</summary>
    [JSInvokable]
    public void OnResume() => _resume.Signal();

    /// <summary>Invoked from JS when connectivity changes.</summary>
    [JSInvokable]
    public void OnConnectivityChanged(bool online)
    {
        _connectivity.SetOnline(online);
        if (online)
        {
            _online.Signal();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                await _module.InvokeVoidAsync("unregister");
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // Circuit already gone (Blazor Server disconnect); nothing to clean up.
        }

        _selfRef?.Dispose();
    }
}
