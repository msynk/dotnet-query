namespace DotNetQuery;

/// <summary>
/// Reports and signals network connectivity. Platform adapters implement this from their native APIs
/// (for example <c>NetworkInformation.GetInternetConnectionProfile</c> on WinUI or
/// <c>Connectivity.Current</c> on MAUI).
/// </summary>
public interface IConnectivityProvider
{
    /// <summary>Gets a value indicating whether the device currently has connectivity.</summary>
    bool IsOnline { get; }

    /// <summary>Raised when connectivity changes. The argument is the new online state.</summary>
    event Action<bool>? ConnectivityChanged;
}

/// <summary>
/// Default adapter-driven <see cref="IConnectivityProvider"/>. Starts online; adapters call
/// <see cref="SetOnline"/> when the platform reports a change.
/// </summary>
public sealed class ConnectivityProvider : IConnectivityProvider
{
    private volatile int _isOnline = 1;

    /// <inheritdoc />
    public bool IsOnline => _isOnline == 1;

    /// <inheritdoc />
    public event Action<bool>? ConnectivityChanged;

    /// <summary>Updates the current connectivity state, raising <see cref="ConnectivityChanged"/> on a transition.</summary>
    public void SetOnline(bool online)
    {
        var next = online ? 1 : 0;
        if (Interlocked.Exchange(ref _isOnline, next) != next)
        {
            ConnectivityChanged?.Invoke(online);
        }
    }
}
