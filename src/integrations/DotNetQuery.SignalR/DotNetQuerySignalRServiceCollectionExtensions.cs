using DotNetQuery;
using DotNetQuery.SignalR;
using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration extensions for DotNetQuery's SignalR integration.</summary>
public static class DotNetQuerySignalRServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="QueryEventSynchronizer"/> fed by a <see cref="HubConnectionQueryEventSource"/> over the
    /// connection <paramref name="connection"/> returns, both with the query client's lifetime (one per app, or one per
    /// scope with a scoped client). Resolve the synchronizer and call <see cref="QueryEventSynchronizer.StartAsync"/> once
    /// at startup; the container disposes both with the client's owner.
    /// </summary>
    /// <param name="builder">The builder returned by <c>AddDotNetQuery</c> (or <c>AddDotNetQueryBlazor</c>).</param>
    /// <param name="connection">
    /// Returns the hub connection. The source never disposes it unless
    /// <see cref="HubConnectionQueryEventSourceOptions.OwnsConnection"/> is set - register a shared connection in the
    /// container, or create a dedicated one here and set <c>OwnsConnection</c>.
    /// </param>
    /// <param name="configure">Registers the event mappings (<see cref="QueryEventSynchronizer.Map{TEvent}"/>).</param>
    /// <param name="sourceOptions">Connection options (group resubscription, restart policy, ownership).</param>
    /// <param name="syncOptions">Synchronization options (deduplication, ordering, recovery, queue).</param>
    /// <example>
    /// <code>
    /// builder.Services.AddDotNetQuery()
    ///     .AddSignalRSync(
    ///         _ => new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect().Build(),
    ///         (_, sync) => sync.Map&lt;Todo&gt;("TodoChanged", m => m.Upsert(TodoQueries.Entity, t => t)),
    ///         new HubConnectionQueryEventSourceOptions { OwnsConnection = true });
    ///
    /// await app.Services.GetRequiredService&lt;QueryEventSynchronizer&gt;().StartAsync();
    /// </code>
    /// </example>
    public static IDotNetQueryBuilder AddSignalRSync(
        this IDotNetQueryBuilder builder,
        Func<IServiceProvider, HubConnection> connection,
        Action<IServiceProvider, QueryEventSynchronizer> configure,
        HubConnectionQueryEventSourceOptions? sourceOptions = null,
        QueryEventSyncOptions? syncOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.TryAdd(ServiceDescriptor.Describe(
            typeof(HubConnectionQueryEventSource),
            sp => new HubConnectionQueryEventSource(connection(sp), sourceOptions),
            builder.ClientLifetime));
        builder.Services.TryAdd(ServiceDescriptor.Describe(
            typeof(QueryEventSynchronizer),
            sp =>
            {
                // Created after the source, so the container disposes the synchronizer first.
                var source = sp.GetRequiredService<HubConnectionQueryEventSource>();
                var synchronizer = new QueryEventSynchronizer(sp.GetRequiredService<IQueryClient>(), source, syncOptions);
                configure(sp, synchronizer);
                return synchronizer;
            },
            builder.ClientLifetime));
        return builder;
    }

    /// <summary>
    /// Registers a SignalR-fed <see cref="QueryEventSynchronizer"/> (see
    /// <see cref="AddSignalRSync(IDotNetQueryBuilder, Func{IServiceProvider, HubConnection}, Action{IServiceProvider, QueryEventSynchronizer}, HubConnectionQueryEventSourceOptions?, QueryEventSyncOptions?)"/>).
    /// </summary>
    public static IDotNetQueryBuilder AddSignalRSync(
        this IDotNetQueryBuilder builder,
        Func<IServiceProvider, HubConnection> connection,
        Action<QueryEventSynchronizer> configure,
        HubConnectionQueryEventSourceOptions? sourceOptions = null,
        QueryEventSyncOptions? syncOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return builder.AddSignalRSync(connection, (_, synchronizer) => configure(synchronizer), sourceOptions, syncOptions);
    }
}
