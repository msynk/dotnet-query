using Microsoft.UI.Xaml;

namespace DotNetQuery.WinUI;

/// <summary>
/// Connects a WinUI 3 window's lifecycle to DotNetQuery: activating the window after another application had the
/// foreground refetches stale active queries (focus), and network availability pauses and resumes queries
/// (connectivity). Create it for the main window at startup; dispose it when the window closes.
/// </summary>
/// <example>
/// <code>
/// // App.xaml.cs OnLaunched, with services.AddDotNetQuery() registered:
/// _window = new MainWindow();
/// _lifecycle = new WinUIQueryLifecycle(_window, services.GetRequiredService&lt;AppLifecycleSignals&gt;());
/// _window.Activate();
/// </code>
/// </example>
public sealed class WinUIQueryLifecycle : IDisposable
{
    private readonly Window _window;
    private readonly AppLifecycleSignals _signals;
    private readonly NetworkConnectivityMonitor? _network;
    private int _disposed;

    /// <summary>Starts forwarding the window's activation and the network availability to <paramref name="signals"/>.</summary>
    /// <param name="window">The application's main window.</param>
    /// <param name="signals">The client's lifecycle signals (registered by <c>AddDotNetQuery</c>).</param>
    /// <param name="monitorNetwork">Report the operating system's network availability (default <see langword="true"/>).</param>
    public WinUIQueryLifecycle(Window window, AppLifecycleSignals signals, bool monitorNetwork = true)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));

        _window.Activated += OnActivated;
        if (monitorNetwork && signals.Connectivity is { } connectivity)
        {
            _network = new NetworkConnectivityMonitor(connectivity);
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) => Forward(_signals, args.WindowActivationState);

    // Separate from the event handler so the mapping is testable without a WinUI window.
    internal static void Forward(AppLifecycleSignals signals, WindowActivationState state)
    {
        if (state is WindowActivationState.Deactivated)
        {
            signals.NotifyDeactivated();
        }
        else
        {
            signals.NotifyActivated();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _window.Activated -= OnActivated;
        _network?.Dispose();
    }
}
