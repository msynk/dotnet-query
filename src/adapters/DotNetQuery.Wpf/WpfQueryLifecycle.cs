using System.Windows;
using Microsoft.Win32;

namespace DotNetQuery.Wpf;

/// <summary>
/// Connects a WPF application's lifecycle to DotNetQuery: switching back to the application refetches stale active
/// queries (focus), waking the machine from sleep refetches them (resume), and network availability pauses and
/// resumes queries (connectivity). Create it once at startup; dispose it on exit.
/// </summary>
/// <example>
/// <code>
/// // App.xaml.cs, with services.AddDotNetQuery() registered:
/// _lifecycle = new WpfQueryLifecycle(this, services.GetRequiredService&lt;AppLifecycleSignals&gt;());
/// </code>
/// </example>
public sealed class WpfQueryLifecycle : IDisposable
{
    private readonly Application _application;
    private readonly AppLifecycleSignals _signals;
    private readonly NetworkConnectivityMonitor? _network;
    private int _disposed;

    /// <summary>Starts forwarding the application's lifecycle events to <paramref name="signals"/>.</summary>
    /// <param name="application">The WPF application.</param>
    /// <param name="signals">The client's lifecycle signals (registered by <c>AddDotNetQuery</c>).</param>
    /// <param name="monitorNetwork">Report the operating system's network availability (default <see langword="true"/>).</param>
    public WpfQueryLifecycle(Application application, AppLifecycleSignals signals, bool monitorNetwork = true)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));

        _application.Activated += OnActivated;
        _application.Deactivated += OnDeactivated;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        if (monitorNetwork && signals.Connectivity is { } connectivity)
        {
            _network = new NetworkConnectivityMonitor(connectivity);
        }
    }

    private void OnActivated(object? sender, EventArgs e) => _signals.NotifyActivated();

    private void OnDeactivated(object? sender, EventArgs e) => _signals.NotifyDeactivated();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Resume)
        {
            _signals.NotifyResumed();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _application.Activated -= OnActivated;
        _application.Deactivated -= OnDeactivated;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged; // a static event: not unsubscribing would leak
        _network?.Dispose();
    }
}
