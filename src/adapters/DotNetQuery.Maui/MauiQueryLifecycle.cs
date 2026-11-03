using System.Text.Json;
using DotNetQuery.Persistence;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace DotNetQuery.Maui;

/// <summary>
/// Connects a MAUI window's lifecycle to DotNetQuery: returning to the application refetches stale active queries
/// (focus), resuming it from the background refetches them (resume), and device connectivity pauses and resumes
/// queries (through a <see cref="MauiConnectivityBridge"/>). Create it for the window in
/// <c>Application.CreateWindow</c>; dispose it when the window is destroyed.
/// </summary>
/// <example>
/// <code>
/// protected override Window CreateWindow(IActivationState? activationState)
/// {
///     var window = new Window(new AppShell());
///     _lifecycle = new MauiQueryLifecycle(window, _services.GetRequiredService&lt;AppLifecycleSignals&gt;());
///     return window;
/// }
/// </code>
/// </example>
public sealed class MauiQueryLifecycle : IDisposable
{
    private readonly Window _window;
    private readonly AppLifecycleSignals _signals;
    private readonly MauiConnectivityBridge? _connectivity;
    private int _disposed;

    /// <summary>Starts forwarding the window's lifecycle events and the device connectivity to <paramref name="signals"/>.</summary>
    /// <param name="window">The application window.</param>
    /// <param name="signals">The client's lifecycle signals (registered by <c>AddDotNetQuery</c>).</param>
    /// <param name="monitorConnectivity">Report the device connectivity (default <see langword="true"/>).</param>
    public MauiQueryLifecycle(Window window, AppLifecycleSignals signals, bool monitorConnectivity = true)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));

        _window.Activated += OnActivated;
        _window.Deactivated += OnDeactivated;
        _window.Resumed += OnResumed;
        _window.Stopped += OnDeactivated;
        if (monitorConnectivity && signals.Connectivity is { } connectivity)
        {
            _connectivity = new MauiConnectivityBridge(connectivity);
        }
    }

    private void OnActivated(object? sender, EventArgs e) => _signals.NotifyActivated();

    private void OnDeactivated(object? sender, EventArgs e) => _signals.NotifyDeactivated();

    private void OnResumed(object? sender, EventArgs e) => _signals.NotifyResumed();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _window.Activated -= OnActivated;
        _window.Deactivated -= OnDeactivated;
        _window.Resumed -= OnResumed;
        _window.Stopped -= OnDeactivated;
        _connectivity?.Dispose();
    }
}

/// <summary>Persistence helpers for MAUI applications.</summary>
public static class MauiQueryPersistence
{
    /// <summary>
    /// Creates a <see cref="FileSystemQueryPersister"/> in the application's private data directory
    /// (<see cref="FileSystem.AppDataDirectory"/>), which survives restarts and is removed with the application. Wrap
    /// it in a <see cref="ProtectedQueryPersister"/> to encrypt sensitive data at rest.
    /// </summary>
    /// <param name="folderName">The folder under the data directory (default <c>dotnetquery</c>).</param>
    /// <param name="jsonOptions">Serializer options for the persisted payloads.</param>
    public static FileSystemQueryPersister CreateFileSystemPersister(string folderName = "dotnetquery", JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        return new FileSystemQueryPersister(Path.Combine(FileSystem.Current.AppDataDirectory, folderName), jsonOptions);
    }
}
