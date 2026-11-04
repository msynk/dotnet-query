using DotNetQuery.Synchronization;
using Microsoft.JSInterop;

namespace DotNetQuery.Blazor;

/// <summary>
/// Relays the registered <see cref="QueryEventSynchronizer"/>'s processed events to the other browser tabs of the same
/// origin over a <c>BroadcastChannel</c> (see <see cref="QueryEventRelay"/>): a tab whose real-time connection lagged
/// or dropped still refreshes the queries those events affect. Registered by <c>AddCrossTabEventRelay</c> and started by
/// <c>&lt;DotNetQueryCrossTabEvents /&gt;</c> once the page is interactive. Without <c>BroadcastChannel</c> support (or
/// while prerendering) nothing is relayed and every tab keeps working on its own.
/// </summary>
public sealed class QueryEventRelayHost : IAsyncDisposable
{
    /// <summary>Creates the relay for <paramref name="synchronizer"/> over the BroadcastChannel named <paramref name="channelName"/>.</summary>
    public QueryEventRelayHost(IJSRuntime js, QueryEventSynchronizer synchronizer, string channelName, QueryEventRelayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(synchronizer);
        Channel = new BroadcastChannelQuerySync(js, channelName);
        Relay = new QueryEventRelay(synchronizer, Channel, options).Start();
    }

    /// <summary>Gets the browser transport.</summary>
    public BroadcastChannelQuerySync Channel { get; }

    /// <summary>Gets the relay (for its counters and <see cref="QueryEventRelay.Error"/> event).</summary>
    public QueryEventRelay Relay { get; }

    /// <summary>Opens the browser channel. Call after the first interactive render; later calls are no-ops.</summary>
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => Channel.StartAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Relay.Dispose();
        await Channel.DisposeAsync().ConfigureAwait(false);
    }
}
