namespace DotNetQuery;

/// <summary>How a query or mutation behaves when the connectivity provider reports the device offline.</summary>
public enum NetworkMode
{
    /// <summary>
    /// Never hit the network while offline: a fetch (or mutation) pauses and resumes on reconnect, and retries stop
    /// when connectivity is lost. The default for queries.
    /// </summary>
    Online,

    /// <summary>
    /// Ignore connectivity entirely: run and retry as if always online. For local or in-process data sources, and
    /// the default for mutations (which then fail rather than wait while offline, matching earlier behavior).
    /// </summary>
    Always,

    /// <summary>
    /// Run the first attempt even while offline (it may be served by an HTTP cache or a service worker), but pause
    /// retries until connectivity returns.
    /// </summary>
    OfflineFirst,
}

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
