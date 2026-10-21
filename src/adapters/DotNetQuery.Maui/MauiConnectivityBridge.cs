using Microsoft.Maui.Networking;

namespace DotNetQuery.Maui;

/// <summary>
/// Drives DotNetQuery's offline pause/resume from MAUI connectivity. Subscribes to
/// <see cref="IConnectivity.ConnectivityChanged"/> and forwards the online state to a
/// <see cref="ConnectivityProvider"/>, so queries pause when the device goes offline and refetch on
/// reconnect. Seeds the current state on construction. Dispose to unsubscribe.
/// </summary>
public sealed class MauiConnectivityBridge : IDisposable
{
    private readonly ConnectivityProvider _provider;
    private readonly IConnectivity _connectivity;
    private int _disposed;

    /// <summary>Creates a bridge over the platform's <see cref="Connectivity.Current"/>.</summary>
    public MauiConnectivityBridge(ConnectivityProvider provider)
        : this(provider, Connectivity.Current)
    {
    }

    /// <summary>Creates a bridge over a specific <see cref="IConnectivity"/> (useful for testing).</summary>
    public MauiConnectivityBridge(ConnectivityProvider provider, IConnectivity connectivity)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));

        _provider.SetOnline(IsOnline(_connectivity.NetworkAccess));
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e) =>
        _provider.SetOnline(IsOnline(e.NetworkAccess));

    private static bool IsOnline(NetworkAccess access) =>
        access is NetworkAccess.Internet or NetworkAccess.ConstrainedInternet;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connectivity.ConnectivityChanged -= OnConnectivityChanged;
    }
}
