using Microsoft.UI.Xaml;

namespace DotNetQuery.Uno;

/// <summary>
/// Connects an Uno Platform app's lifecycle to DotNetQuery on every Uno target: activating the window after another
/// application had the foreground refetches stale active queries (focus), returning from the background on mobile and
/// WebAssembly refetches them (resume), and - where the operating system reports it - network availability pauses and
/// resumes queries (connectivity). Create it for the main window at startup; dispose it when the window closes.
/// </summary>
/// <example>
/// <code>
/// // App.xaml.cs OnLaunched, with services.AddDotNetQuery() registered:
/// MainWindow = new Window();
/// _lifecycle = new UnoQueryLifecycle(MainWindow, this, Host.Services.GetRequiredService&lt;AppLifecycleSignals&gt;());
/// </code>
/// </example>
public sealed class UnoQueryLifecycle : IDisposable
{
    private readonly Window _window;
    private readonly Application? _application;
    private readonly AppLifecycleSignals _signals;
    private readonly NetworkConnectivityMonitor? _network;
    private int _inBackground;
    private int _disposed;

    /// <summary>Starts forwarding the window's activation, the application's background transitions and the network availability.</summary>
    /// <param name="window">The application's main window.</param>
    /// <param name="application">The application (for resume from the background), or <see langword="null"/> to skip it.</param>
    /// <param name="signals">The client's lifecycle signals (registered by <c>AddDotNetQuery</c>).</param>
    /// <param name="monitorNetwork">
    /// Report the operating system's network availability (default <see langword="true"/>; ignored in the browser, where
    /// the app reports connectivity itself through <see cref="ConnectivityProvider.SetOnline"/>).
    /// </param>
    public UnoQueryLifecycle(Window window, Application? application, AppLifecycleSignals signals, bool monitorNetwork = true)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _application = application;
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));

        _window.Activated += OnActivated;
        if (_application is not null)
        {
            _application.EnteredBackground += OnEnteredBackground;
            _application.LeavingBackground += OnLeavingBackground;
        }

        if (monitorNetwork && !OperatingSystem.IsBrowser() && signals.Connectivity is { } connectivity)
        {
            _network = new NetworkConnectivityMonitor(connectivity);
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) => Forward(_signals, args.WindowActivationState);

    private void OnEnteredBackground(object? sender, object e)
    {
        Volatile.Write(ref _inBackground, 1);
        _signals.NotifyDeactivated();
    }

    // Also raised while launching; only a return from the background is a resume.
    private void OnLeavingBackground(object? sender, object e)
    {
        if (Interlocked.Exchange(ref _inBackground, 0) == 1)
        {
            _signals.NotifyResumed();
        }
    }

    // Separate from the event handler so the mapping is testable without a window.
    internal static void Forward(AppLifecycleSignals signals, Windows.UI.Core.CoreWindowActivationState state)
    {
        if (state is Windows.UI.Core.CoreWindowActivationState.Deactivated)
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
        if (_application is not null)
        {
            _application.EnteredBackground -= OnEnteredBackground;
            _application.LeavingBackground -= OnLeavingBackground;
        }

        _network?.Dispose();
    }
}
