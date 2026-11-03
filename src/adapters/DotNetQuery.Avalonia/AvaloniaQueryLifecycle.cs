using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace DotNetQuery.Avalonia;

/// <summary>
/// Connects an Avalonia application's lifecycle to DotNetQuery: returning to the application refetches stale active
/// queries (focus), resuming a mobile application from the background refetches them (resume), and network
/// availability pauses and resumes queries (connectivity). Create it once at startup; dispose it on exit.
/// </summary>
/// <remarks>
/// Desktop applications attach to their main window (window activation). Mobile and macOS applications attach to
/// the <see cref="global::Avalonia.Application"/>, which follows the platform's activation lifetime
/// (<see cref="IActivatableLifetime"/>).
/// </remarks>
/// <example>
/// <code>
/// // App.axaml.cs OnFrameworkInitializationCompleted, with services.AddDotNetQuery() registered:
/// var signals = services.GetRequiredService&lt;AppLifecycleSignals&gt;();
/// if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
///     _lifecycle = new AvaloniaQueryLifecycle(desktop.MainWindow!, signals);
/// else
///     _lifecycle = new AvaloniaQueryLifecycle(this, signals);
/// </code>
/// </example>
public sealed class AvaloniaQueryLifecycle : IDisposable
{
    private readonly AppLifecycleSignals _signals;
    private readonly WindowBase? _window;
    private readonly IActivatableLifetime? _activatable;
    private readonly NetworkConnectivityMonitor? _network;
    private int _disposed;

    /// <summary>Starts forwarding a window's activation (desktop) and the network availability to <paramref name="signals"/>.</summary>
    /// <param name="window">The application's main window.</param>
    /// <param name="signals">The client's lifecycle signals (registered by <c>AddDotNetQuery</c>).</param>
    /// <param name="monitorNetwork">Report the operating system's network availability (default <see langword="true"/>).</param>
    public AvaloniaQueryLifecycle(WindowBase window, AppLifecycleSignals signals, bool monitorNetwork = true)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _window.Activated += OnWindowActivated;
        _window.Deactivated += OnWindowDeactivated;
        _network = monitorNetwork ? TryMonitorNetwork(signals) : null;
    }

    /// <summary>
    /// Starts forwarding the application's activation lifetime (mobile, macOS) and the network availability to
    /// <paramref name="signals"/>. On platforms without an activation lifetime only the network is reported.
    /// </summary>
    /// <param name="application">The Avalonia application.</param>
    /// <param name="signals">The client's lifecycle signals (registered by <c>AddDotNetQuery</c>).</param>
    /// <param name="monitorNetwork">Report the operating system's network availability (default <see langword="true"/>).</param>
    public AvaloniaQueryLifecycle(global::Avalonia.Application application, AppLifecycleSignals signals, bool monitorNetwork = true)
    {
        ArgumentNullException.ThrowIfNull(application);
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _activatable = application.TryGetFeature(typeof(IActivatableLifetime)) as IActivatableLifetime;
        if (_activatable is not null)
        {
            _activatable.Activated += OnApplicationActivated;
            _activatable.Deactivated += OnApplicationDeactivated;
        }

        _network = monitorNetwork ? TryMonitorNetwork(signals) : null;
    }

    private void OnWindowActivated(object? sender, EventArgs e) => _signals.NotifyActivated();

    private void OnWindowDeactivated(object? sender, EventArgs e) => _signals.NotifyDeactivated();

    private void OnApplicationActivated(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind is ActivationKind.Background)
        {
            _signals.NotifyResumed(); // back from the background
        }
        else
        {
            _signals.NotifyActivated();
        }
    }

    private void OnApplicationDeactivated(object? sender, ActivatedEventArgs e) => _signals.NotifyDeactivated();

    private static NetworkConnectivityMonitor? TryMonitorNetwork(AppLifecycleSignals signals)
    {
        if (signals.Connectivity is not { } connectivity || OperatingSystem.IsBrowser())
        {
            return null;
        }

        try
        {
            return new NetworkConnectivityMonitor(connectivity);
        }
        catch (PlatformNotSupportedException)
        {
            return null; // the platform reports no network changes; connectivity stays as the app sets it
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
        }

        if (_activatable is not null)
        {
            _activatable.Activated -= OnApplicationActivated;
            _activatable.Deactivated -= OnApplicationDeactivated;
        }

        _network?.Dispose();
    }
}
