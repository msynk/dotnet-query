namespace DotNetQuery.Synchronization;

/// <summary>
/// Describes how one named event changes the cache: which events it accepts, how to read their metadata, and the cache
/// operations to apply - invalidations, typed data updates, entity upserts and removals, or custom handlers. Configure it
/// in the callback of <see cref="QueryEventSynchronizer.Map{TEvent}"/>; it cannot be changed afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Data operations (<see cref="SetData"/>, <see cref="Update{TData}(QueryKey, Func{TData, TEvent, TData})"/>,
/// <see cref="Upsert"/>, <see cref="Remove"/>) of one mapping are applied atomically: observers see all or none of them.
/// They never create cache entries unless asked to (<see cref="SetData"/> with <c>createIfMissing</c>), and only touch
/// queries that hold data. A query whose fetch started before the event is refetched afterwards, so the older response
/// cannot overwrite the update. Invalidations and handlers run after the data operations, in the order declared.
/// </para>
/// <para>
/// If any operation throws, the mapping's writes are discarded, the queries it targets are invalidated instead, and the
/// failure is reported - other mappings of the same event are unaffected.
/// </para>
/// </remarks>
/// <typeparam name="TEvent">The event payload type.</typeparam>
public sealed class QueryEventMapping<TEvent>
{
    private readonly List<MappingAction<TEvent>> _actions = [];
    private readonly List<Func<TEvent, bool>> _filters = [];
    private readonly List<QueryKey> _covers = [];
    private bool _sealed;

    internal QueryEventMapping(string eventName) => EventName = eventName;

    /// <summary>Gets the name of the event this mapping handles.</summary>
    public string EventName { get; }

    internal Func<TEvent, QueryEventMetadata>? Metadata { get; private set; }

    internal bool IgnoresLocalEchoes { get; private set; }

    internal bool RecoveryOptOut { get; private set; }

    internal IReadOnlyList<MappingAction<TEvent>> Actions => _actions;

    /// <summary>Accepts only events matching <paramref name="predicate"/> (several conditions must all hold).</summary>
    public QueryEventMapping<TEvent> Where(Func<TEvent, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ThrowIfSealed();
        _filters.Add(predicate);
        return this;
    }

    /// <summary>
    /// Reads each event's metadata (id, entity version, sequence, scope, correlation) for duplicate detection and
    /// ordering. Payloads implementing <see cref="IQueryEventMetadataProvider"/> (such as
    /// <see cref="QueryEventEnvelope{TPayload}"/>) need no extractor. Every mapping of one event shares the extractor, so
    /// declare it on at most one of them.
    /// </summary>
    public QueryEventMapping<TEvent> WithMetadata(Func<TEvent, QueryEventMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ThrowIfSealed();
        if (Metadata is not null)
        {
            throw new InvalidOperationException($"The metadata of event '{EventName}' is already configured.");
        }

        Metadata = metadata;
        return this;
    }

    /// <summary>
    /// Declares key prefixes this mapping can affect beyond its static keys, so a recovery (after a reconnect, gap or
    /// overflow) invalidates them. Required when the mapping only targets keys computed from the event.
    /// </summary>
    public QueryEventMapping<TEvent> Covers(params QueryKey[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ThrowIfSealed();
        foreach (var key in keys)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(keys));
            _covers.Add(key);
        }

        return this;
    }

    /// <summary>
    /// Declares that a recovery has nothing to reconcile for this mapping (for example a handler that only shows a
    /// notification), so it may target keys computed from the event without <see cref="Covers"/>.
    /// </summary>
    public QueryEventMapping<TEvent> NoRecovery()
    {
        ThrowIfSealed();
        RecoveryOptOut = true;
        return this;
    }

    /// <summary>
    /// Skips this mapping for events whose <see cref="QueryEventMetadata.CorrelationId"/> was registered with
    /// <see cref="QueryEventSynchronizer.MarkLocalOrigin"/> - the echo of a change this client made and already reconciles
    /// itself (with its own response or invalidation). Correlation is by operation, never by user: changes from other
    /// sessions of the same user are still applied.
    /// </summary>
    public QueryEventMapping<TEvent> IgnoreLocalEchoes()
    {
        ThrowIfSealed();
        IgnoresLocalEchoes = true;
        return this;
    }

    /// <summary>Invalidates every query under <paramref name="key"/> (prefix match); observed ones refetch.</summary>
    public QueryEventMapping<TEvent> Invalidate(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Add(new InvalidateAction<TEvent>(key, null, null));
    }

    /// <summary>Invalidates every query under the key computed from the event (prefix match).</summary>
    public QueryEventMapping<TEvent> Invalidate(Func<TEvent, QueryKey> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Add(new InvalidateAction<TEvent>(null, key, null));
    }

    /// <summary>Invalidates every query matching the filter computed from the event (for example an exact key).</summary>
    public QueryEventMapping<TEvent> Invalidate(Func<TEvent, QueryFilter> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Add(new InvalidateAction<TEvent>(null, null, filter));
    }

    /// <summary>
    /// Replaces the data of the query under the key computed from the event (exact match) - for events carrying the
    /// complete, authoritative value. Only a query that holds data is written, unless <paramref name="createIfMissing"/>
    /// is set (the event then seeds the cache).
    /// </summary>
    public QueryEventMapping<TEvent> SetData<TData>(Func<TEvent, QueryKey> key, Func<TEvent, TData> data, bool createIfMissing = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        return Add(new SetDataAction<TEvent, TData>(key, data, createIfMissing));
    }

    /// <summary>
    /// Transforms the data of every query of type <typeparamref name="TData"/> under <paramref name="key"/> (prefix match)
    /// that holds data. Return the current value unchanged to skip a query (it is then not written or re-rendered); return
    /// a new instance otherwise, never mutate the current one. The function may run more than once if a query changes
    /// concurrently, so keep it pure.
    /// </summary>
    public QueryEventMapping<TEvent> Update<TData>(QueryKey key, Func<TData, TEvent, TData> update)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(update);
        return Add(new UpdateAction<TEvent, TData>(key, null, update));
    }

    /// <summary>
    /// Transforms the data of every query of type <typeparamref name="TData"/> that matches the filter computed from the
    /// event and holds data (see <see cref="Update{TData}(QueryKey, Func{TData, TEvent, TData})"/>).
    /// </summary>
    public QueryEventMapping<TEvent> Update<TData>(Func<TEvent, QueryFilter> filter, Func<TData, TEvent, TData> update)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(update);
        return Add(new UpdateAction<TEvent, TData>(null, filter, update));
    }

    /// <summary>
    /// Inserts or replaces the entity carried by the event in every cached shape of <paramref name="entity"/> under its
    /// key: detail queries holding it are replaced, lists and pages holding it get it replaced in place, and lists
    /// without it follow <paramref name="whenMissing"/>. With a version on the entity, a cached item is only replaced by
    /// a newer one.
    /// </summary>
    public QueryEventMapping<TEvent> Upsert<TItem, TId>(
        QueryEntity<TItem, TId> entity,
        Func<TEvent, TItem> item,
        EntityMissingBehavior whenMissing = EntityMissingBehavior.Invalidate)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(item);
        return Add(new EntityAction<TEvent, TItem, TId>(entity, e => new UpsertOperation<TItem, TId>(entity, item(e), whenMissing)));
    }

    /// <summary>
    /// Removes the entity whose id the event carries from every cached list and page of <paramref name="entity"/>, and
    /// handles its detail queries as <paramref name="detail"/> says.
    /// </summary>
    public QueryEventMapping<TEvent> Remove<TItem, TId>(
        QueryEntity<TItem, TId> entity,
        Func<TEvent, TId> id,
        EntityDetailRemoval detail = EntityDetailRemoval.Invalidate)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(id);
        return Add(new EntityAction<TEvent, TItem, TId>(entity, e => new RemoveOperation<TItem, TId>(entity, id(e), detail)));
    }

    /// <summary>
    /// Runs application-defined logic for cache shapes the built-in operations do not cover. Handlers run one event at a
    /// time, after the mapping's data operations; keep them short, since later events wait for them.
    /// </summary>
    public QueryEventMapping<TEvent> Handle(Func<QueryEventContext<TEvent>, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new HandleAction<TEvent>(handler));
    }

    /// <summary>Runs a synchronous application-defined handler (see <see cref="Handle(Func{QueryEventContext{TEvent}, CancellationToken, ValueTask})"/>).</summary>
    public QueryEventMapping<TEvent> Handle(Action<QueryEventContext<TEvent>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new HandleAction<TEvent>((context, _) =>
        {
            handler(context);
            return default;
        }));
    }

    private QueryEventMapping<TEvent> Add(MappingAction<TEvent> action)
    {
        ThrowIfSealed();
        _actions.Add(action);
        return this;
    }

    private void ThrowIfSealed()
    {
        if (_sealed)
        {
            throw new InvalidOperationException("A mapping cannot be changed after it was registered.");
        }
    }

    /// <summary>Validates and freezes the configuration; returns the key prefixes a recovery must invalidate.</summary>
    internal IReadOnlyList<QueryKey> Seal()
    {
        _sealed = true;
        if (_actions.Count == 0)
        {
            throw new InvalidOperationException($"The mapping for event '{EventName}' has no operation.");
        }

        var covered = new List<QueryKey>(_covers);
        var dynamic = false;
        foreach (var action in _actions)
        {
            if (action.StaticKey is { } key)
            {
                covered.Add(key);
            }
            else
            {
                dynamic = true;
            }
        }

        if (dynamic && covered.Count == 0 && !RecoveryOptOut)
        {
            throw new InvalidOperationException(
                $"The mapping for event '{EventName}' targets keys computed from the event, which a recovery cannot " +
                "derive. Declare the key prefixes it can affect with Covers(...), or call NoRecovery() if a recovery has " +
                "nothing to reconcile.");
        }

        return RecoveryOptOut && _covers.Count == 0 ? [] : covered;
    }

    internal bool Accepts(TEvent payload)
    {
        foreach (var filter in _filters)
        {
            if (!filter(payload))
            {
                return false;
            }
        }

        return true;
    }

    internal async ValueTask ApplyAsync(QueryEventSynchronizer owner, TEvent payload, QueryEventMetadata metadata, long changeSequence, bool replay, CancellationToken cancellationToken)
    {
        var cache = owner.Client.Cache;
        QueryDataBatch? batch = null;
        List<EntityWrite>? writes = null;
        List<BoundScan>? scans = null;

        foreach (var action in _actions)
        {
            if (action is DataAction<TEvent> data)
            {
                batch ??= new QueryDataBatch();
                writes ??= [];
                data.Collect(cache, payload, batch, writes, ref scans);
            }
        }

        if (scans is not null)
        {
            // One pass over the cache for every operation of the mapping that matches by key prefix.
            foreach (var entry in cache.EnumerateEntries())
            {
                foreach (var scan in scans)
                {
                    scan.TryAdd(entry, batch!, writes!);
                }
            }
        }

        if (batch is { Writes.Count: > 0 })
        {
            foreach (var write in batch.Writes)
            {
                write.SkipUnchanged = true;
            }

            QueryDataTransaction.Commit(cache, batch.Writes);
        }

        if (writes is not null)
        {
            foreach (var write in writes)
            {
                if (write.NeedsRemoval)
                {
                    owner.Client.RemoveQuery(write.Entry.Key);
                }
                else if (write.NeedsInvalidate)
                {
                    owner.QueueInvalidation(QueryFilter.ForKey(write.Entry.Key, exact: true));
                }
                else if (write.Relevant && write.Entry.IsFetching)
                {
                    // The fetch may have read the server before this change: cancel and restart it, unless it started
                    // after the event was received (it then already reflects the change).
                    owner.Supersede(write.Entry, changeSequence);
                }
            }
        }

        QueryEventContext<TEvent>? context = null;
        foreach (var action in _actions)
        {
            switch (action)
            {
                case InvalidateAction<TEvent> invalidate:
                    owner.QueueInvalidation(invalidate.Target(payload));
                    break;
                case HandleAction<TEvent> handle:
                    context ??= new QueryEventContext<TEvent>(owner, EventName, payload, metadata, replay);
                    await handle.Handler(context, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>After a failure: invalidates what the mapping targets (best effort) and what it covers.</summary>
    internal void InvalidateTargets(QueryEventSynchronizer owner, TEvent payload)
    {
        foreach (var action in _actions)
        {
            try
            {
                if (action.FallbackTarget(payload) is { } target)
                {
                    owner.QueueInvalidation(target);
                }
            }
            catch (Exception)
            {
                // The target itself cannot be computed from this event; the covered keys below still apply.
            }
        }

        foreach (var key in _covers)
        {
            owner.QueueInvalidation(QueryFilter.ForKey(key));
        }
    }
}

/// <summary>What a <see cref="QueryEventMapping{TEvent}.Handle(Func{QueryEventContext{TEvent}, CancellationToken, ValueTask})"/> handler can see and do.</summary>
/// <typeparam name="TEvent">The event payload type.</typeparam>
public sealed class QueryEventContext<TEvent>
{
    private readonly QueryEventSynchronizer _owner;

    internal QueryEventContext(QueryEventSynchronizer owner, string eventName, TEvent @event, QueryEventMetadata metadata, bool isReplay)
    {
        _owner = owner;
        EventName = eventName;
        Event = @event;
        Metadata = metadata;
        IsReplay = isReplay;
    }

    /// <summary>Gets the event name.</summary>
    public string EventName { get; }

    /// <summary>Gets the event payload.</summary>
    public TEvent Event { get; }

    /// <summary>Gets the event metadata.</summary>
    public QueryEventMetadata Metadata { get; }

    /// <summary>Gets whether the event is being replayed by a recovery rather than received live.</summary>
    public bool IsReplay { get; }

    /// <summary>Gets the query client.</summary>
    public IQueryClient Client => _owner.Client;

    /// <summary>Invalidates every query under <paramref name="key"/>, coalesced with the other invalidations of the burst.</summary>
    public void Invalidate(QueryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _owner.QueueInvalidation(QueryFilter.ForKey(key));
    }

    /// <summary>Invalidates every query matching <paramref name="filter"/>, coalesced with the other invalidations of the burst.</summary>
    public void Invalidate(QueryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _owner.QueueInvalidation(filter);
    }
}

internal abstract class MappingAction<TEvent>
{
    /// <summary>The key prefix the action always targets, or <see langword="null"/> when its targets depend on the event.</summary>
    public virtual QueryKey? StaticKey => null;

    /// <summary>What to invalidate when the mapping failed for <paramref name="payload"/>.</summary>
    public virtual QueryFilter? FallbackTarget(TEvent payload) => null;
}

internal sealed class InvalidateAction<TEvent>(QueryKey? key, Func<TEvent, QueryKey>? keyOf, Func<TEvent, QueryFilter>? filterOf) : MappingAction<TEvent>
{
    public override QueryKey? StaticKey => key;

    public QueryFilter Target(TEvent payload) =>
        key is not null ? QueryFilter.ForKey(key)
        : keyOf is not null ? QueryFilter.ForKey(keyOf(payload) ?? throw new InvalidOperationException("The key selector returned null."))
        : filterOf!(payload) ?? throw new InvalidOperationException("The filter selector returned null.");

    public override QueryFilter? FallbackTarget(TEvent payload) => Target(payload);
}

internal sealed class HandleAction<TEvent>(Func<QueryEventContext<TEvent>, CancellationToken, ValueTask> handler) : MappingAction<TEvent>
{
    public Func<QueryEventContext<TEvent>, CancellationToken, ValueTask> Handler { get; } = handler;
}

/// <summary>An action writing data: added to the mapping's transaction directly or through a cache scan.</summary>
internal abstract class DataAction<TEvent> : MappingAction<TEvent>
{
    public abstract void Collect(QueryCache cache, TEvent payload, QueryDataBatch batch, List<EntityWrite> writes, ref List<BoundScan>? scans);
}

/// <summary>A data action bound to one event, matched against every cache entry.</summary>
internal abstract class BoundScan
{
    public abstract void TryAdd(IQueryEntry entry, QueryDataBatch batch, List<EntityWrite> writes);
}

internal sealed class SetDataAction<TEvent, TData>(Func<TEvent, QueryKey> keyOf, Func<TEvent, TData> dataOf, bool createIfMissing) : DataAction<TEvent>
{
    public override QueryFilter? FallbackTarget(TEvent payload) => QueryFilter.ForKey(keyOf(payload), exact: true);

    public override void Collect(QueryCache cache, TEvent payload, QueryDataBatch batch, List<EntityWrite> writes, ref List<BoundScan>? scans)
    {
        var key = keyOf(payload) ?? throw new InvalidOperationException("The key selector returned null.");
        var value = dataOf(payload);
        if (createIfMissing)
        {
            var entry = cache.GetOrCreate<TData>(key);
            batch.Add<TData>(key, _ => value, entry: null);
            writes.Add(new EntityWrite(entry));
            return;
        }

        if (cache.TryGetEntry(key, out var existing))
        {
            if (existing is not QueryEntry<TData> typed)
            {
                throw new InvalidOperationException(
                    $"Query {key} holds {existing.Describe().DataTypeName}, not {TypeKeyName<TData>.Value}.");
            }

            batch.Add<TData>(key, _ => value, typed);
            writes.Add(new EntityWrite(existing));
        }
    }
}

internal sealed class UpdateAction<TEvent, TData>(QueryKey? prefix, Func<TEvent, QueryFilter>? filterOf, Func<TData, TEvent, TData> update) : DataAction<TEvent>
{
    public override QueryKey? StaticKey => prefix;

    public override QueryFilter? FallbackTarget(TEvent payload) => prefix is not null ? QueryFilter.ForKey(prefix) : filterOf!(payload);

    public override void Collect(QueryCache cache, TEvent payload, QueryDataBatch batch, List<EntityWrite> writes, ref List<BoundScan>? scans)
    {
        var filter = prefix is not null ? null : filterOf!(payload) ?? throw new InvalidOperationException("The filter selector returned null.");
        (scans ??= []).Add(new Scan(prefix, filter, payload, update));
    }

    private sealed class Scan(QueryKey? prefix, QueryFilter? filter, TEvent payload, Func<TData, TEvent, TData> update) : BoundScan
    {
        public override void TryAdd(IQueryEntry entry, QueryDataBatch batch, List<EntityWrite> writes)
        {
            if (entry is not QueryEntry<TData> typed || !(prefix is not null ? prefix.IsPrefixOf(entry.Key) : entry.Matches(filter!)))
            {
                return;
            }

            batch.Add<TData>(entry.Key, data => update(data!, payload), typed);
            writes.Add(new EntityWrite(entry));
        }
    }
}

internal sealed class EntityAction<TEvent, TItem, TId>(QueryEntity<TItem, TId> entity, Func<TEvent, EntityOperation<TItem, TId>> bind) : DataAction<TEvent>
{
    public override QueryKey? StaticKey => entity.Key;

    public override QueryFilter? FallbackTarget(TEvent payload) => QueryFilter.ForKey(entity.Key);

    public override void Collect(QueryCache cache, TEvent payload, QueryDataBatch batch, List<EntityWrite> writes, ref List<BoundScan>? scans)
    {
        var operation = bind(payload);
        if (entity.DetailKey is { } detailKey && cache.TryGetEntry(detailKey(operation.Id), out var detail))
        {
            DetailShape<TItem, TId>.Instance.TryAdd(detail, operation, batch, writes);
        }

        (scans ??= []).Add(new Scan(operation));
    }

    private sealed class Scan(EntityOperation<TItem, TId> operation) : BoundScan
    {
        public override void TryAdd(IQueryEntry entry, QueryDataBatch batch, List<EntityWrite> writes)
        {
            var entity = operation.Entity;
            if (!entity.Key.IsPrefixOf(entry.Key))
            {
                return;
            }

            if (entry is QueryEntry<TItem>)
            {
                // Detail queries: found by key above when the entity names its detail key, by the data's id otherwise.
                if (entity.DetailKey is null)
                {
                    DetailShape<TItem, TId>.Instance.TryAdd(entry, operation, batch, writes);
                }

                return;
            }

            foreach (var shape in entity.Shapes)
            {
                if (shape.TryAdd(entry, operation, batch, writes))
                {
                    return;
                }
            }
        }
    }
}
