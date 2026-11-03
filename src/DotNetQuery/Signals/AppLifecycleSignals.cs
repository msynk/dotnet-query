using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace DotNetQuery;

/// <summary>
/// Maps an application's lifecycle onto the query client's signals, so every UI stack refetches the same way:
/// regaining focus after being in the background signals <see cref="FocusManager"/>, resuming from suspension
/// signals <see cref="ResumeManager"/>, and network availability updates <see cref="ConnectivityProvider"/>.
/// The platform adapters (WPF, WinUI, Avalonia, MAUI) wire their native events to it; any other UI stack can call
/// the <c>Notify*</c> methods from its own events.
/// </summary>
/// <remarks>
/// Only a real return counts: activation is signalled only after a deactivation (the first activation at startup,
/// or switching between the application's own windows, does not refetch), and an activation that follows a resume
/// is not signalled again. <c>services.AddDotNetQuery()</c> registers an instance wired to the client's managers.
/// </remarks>
public sealed class AppLifecycleSignals
{
    private readonly FocusManager? _focus;
    private readonly ResumeManager? _resume;
    private int _active = 1;

    /// <summary>Creates lifecycle signals over the client's managers (each optional).</summary>
    /// <param name="focus">Signalled when the application regains focus.</param>
    /// <param name="resume">Signalled when the application resumes from the background.</param>
    /// <param name="connectivity">Updated with network availability.</param>
    public AppLifecycleSignals(FocusManager? focus = null, ResumeManager? resume = null, ConnectivityProvider? connectivity = null)
    {
        _focus = focus;
        _resume = resume;
        Connectivity = connectivity;
    }

    /// <summary>Gets the connectivity provider network availability is reported to, if any.</summary>
    public ConnectivityProvider? Connectivity { get; }

    /// <summary>Gets a value indicating whether the application is currently in the foreground.</summary>
    public bool IsActive => Volatile.Read(ref _active) == 1;

    /// <summary>The application (or one of its windows) became the foreground application.</summary>
    public void NotifyActivated()
    {
        if (Interlocked.Exchange(ref _active, 1) == 0)
        {
            _focus?.Signal();
        }
    }

    /// <summary>The application lost the foreground (another application was activated, or it was minimized).</summary>
    public void NotifyDeactivated() => Volatile.Write(ref _active, 0);

    /// <summary>The application resumed from suspension or the background (mobile resume, system wake).</summary>
    public void NotifyResumed()
    {
        Volatile.Write(ref _active, 1);
        _resume?.Signal();
    }

    /// <summary>Reports whether the network is available.</summary>
    public void NotifyNetworkAvailability(bool available) => Connectivity?.SetOnline(available);
}

/// <summary>
/// Drives a <see cref="ConnectivityProvider"/> from the operating system's network availability
/// (<see cref="NetworkChange.NetworkAvailabilityChanged"/>), so queries pause while the machine is offline and
/// refetch when it reconnects. Seeds the current state on construction; dispose to stop listening.
/// </summary>
/// <remarks>
/// The operating system reports whether any network interface is up, not whether a particular server is reachable;
/// combine it with retries (or an application-level health check calling <see cref="ConnectivityProvider.SetOnline"/>)
/// when that distinction matters. Not supported in the browser - Blazor WebAssembly uses the browser's
/// online/offline events instead.
/// </remarks>
[UnsupportedOSPlatform("browser")]
public sealed class NetworkConnectivityMonitor : IDisposable
{
    private readonly ConnectivityProvider _provider;
    private int _disposed;

    /// <summary>Starts reporting network availability to <paramref name="provider"/>.</summary>
    public NetworkConnectivityMonitor(ConnectivityProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        _provider.SetOnline(NetworkInterface.GetIsNetworkAvailable());
    }

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _provider.SetOnline(e.IsAvailable);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        }
    }
}
