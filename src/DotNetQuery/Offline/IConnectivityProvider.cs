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
    private readonly object _gate = new();
    private volatile bool _isOnline = true;

    /// <inheritdoc />
    public bool IsOnline => _isOnline;

    /// <inheritdoc />
    public event Action<bool>? ConnectivityChanged;

    /// <summary>
    /// Updates the current connectivity state, raising <see cref="ConnectivityChanged"/> on a transition.
    /// Transitions are serialized, so subscribers always observe them in order and the last value raised is
    /// the current state, even when the platform reports changes from several threads.
    /// </summary>
    public void SetOnline(bool online)
    {
        lock (_gate)
        {
            if (_isOnline == online)
            {
                return;
            }

            _isOnline = online;
            ConnectivityChanged?.Invoke(online);
        }
    }
}
