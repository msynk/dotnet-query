using DotNetQuery;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace TodoApp;

/// <summary>
/// Blazor Server reconnection: when the browser's connection to the circuit comes back, refetch the stale queries
/// the user is looking at - the browser's own online/offline events do not cover a dropped SignalR connection.
/// </summary>
public sealed class QueryCircuitHandler(IQueryClient client) : CircuitHandler
{
    private bool _wasDown;

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _wasDown = true;
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_wasDown)
        {
            _wasDown = false;
            _ = client.RefetchQueriesAsync(new QueryFilter { Type = QueryTypeFilter.Active, Stale = true }, CancellationToken.None);
        }

        return Task.CompletedTask;
    }
}
