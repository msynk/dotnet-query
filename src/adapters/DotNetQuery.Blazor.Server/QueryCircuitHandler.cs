using DotNetQuery;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetQuery.Blazor.Server
{
    /// <summary>
    /// Blazor Server reconnection: when the browser's connection to a circuit comes back after dropping, refetches the
    /// stale queries the user is looking at. The browser's own online/offline events (<c>DotNetQueryBrowserSignals</c>)
    /// do not cover a dropped SignalR connection, and while it was down the circuit's queries could not be refreshed.
    /// Registered by <c>RefetchOnCircuitReconnect()</c>; one instance per circuit.
    /// </summary>
    public sealed class QueryCircuitHandler : CircuitHandler
    {
        private readonly IQueryClient _client;
        private readonly QueryCircuitHandlerOptions _options;
        private int _wasDown;

        /// <summary>Creates the handler for the circuit's <paramref name="client"/>.</summary>
        public QueryCircuitHandler(IQueryClient client, QueryCircuitHandlerOptions? options = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _options = options ?? new QueryCircuitHandlerOptions();
        }

        /// <inheritdoc />
        public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            Volatile.Write(ref _wasDown, 1);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _wasDown, 0) == 1)
            {
                // Fire and forget: the reconnection must not wait for the network. Failures land in the queries' state.
                _ = RefetchAsync();
            }

            return Task.CompletedTask;
        }

        /// <summary>Gets a task that completes when the refetch started by the last reconnection finished (for tests).</summary>
        internal Task LastRefetch { get; private set; } = Task.CompletedTask;

        private Task RefetchAsync()
        {
            var task = RefetchCoreAsync();
            LastRefetch = task;
            return task;
        }

        private async Task RefetchCoreAsync()
        {
            try
            {
                if (_options.Invalidate)
                {
                    await _client.InvalidateQueriesAsync(_options.Filter, RefetchType.Active, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _client.RefetchQueriesAsync(_options.Filter, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The client was disposed with the circuit, or a filter predicate threw: nothing to refresh.
            }
        }
    }

    /// <summary>Options for <see cref="QueryCircuitHandler"/>.</summary>
    public sealed record QueryCircuitHandlerOptions
    {
        /// <summary>
        /// The queries to refresh on reconnection. Defaults to the active (rendered) stale ones - fresh data is still
        /// within its <c>StaleTime</c> and needs no request.
        /// </summary>
        public QueryFilter Filter { get; init; } = new() { Type = QueryTypeFilter.Active, Stale = true };

        /// <summary>
        /// When <see langword="true"/>, the matching queries are invalidated (marked stale, active ones refetched)
        /// instead of only refetched - use with a broader <see cref="Filter"/> to also refresh fresh data the server may
        /// have changed while the connection was down. Defaults to <see langword="false"/>.
        /// </summary>
        public bool Invalidate { get; init; }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Blazor Server registration for DotNetQuery.</summary>
    public static class DotNetQueryBlazorServerBuilderExtensions
    {
        /// <summary>
        /// Registers <see cref="DotNetQuery.Blazor.Server.QueryCircuitHandler"/>: when a dropped Blazor Server connection
        /// comes back, the stale queries on screen are refetched. Call on the server project's
        /// <c>AddDotNetQueryBlazor()</c> builder.
        /// </summary>
        public static IDotNetQueryBuilder RefetchOnCircuitReconnect(this IDotNetQueryBuilder builder, DotNetQuery.Blazor.Server.QueryCircuitHandlerOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            var resolved = options ?? new DotNetQuery.Blazor.Server.QueryCircuitHandlerOptions();
            builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, DotNetQuery.Blazor.Server.QueryCircuitHandler>(
                sp => new DotNetQuery.Blazor.Server.QueryCircuitHandler(sp.GetRequiredService<IQueryClient>(), resolved)));
            return builder;
        }
    }
}
