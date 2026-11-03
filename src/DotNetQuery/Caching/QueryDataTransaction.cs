namespace DotNetQuery;

/// <summary>
/// Collects cache writes for <see cref="QueryDataTransactionExtensions.SetQueriesData(IQueryClient, Action{QueryDataBatch})"/>,
/// which applies all of them atomically. Writes to the same key are applied in the order they were added.
/// </summary>
public sealed class QueryDataBatch
{
    private readonly List<BatchWrite> _writes = [];

    internal QueryDataBatch()
    {
    }

    internal IReadOnlyList<BatchWrite> Writes => _writes;

    /// <summary>Replaces the data of the query under <paramref name="key"/> (creating the entry if needed).</summary>
    public QueryDataBatch Set<T>(QueryKey key, T data) => Update<T>(key, _ => data);

    /// <summary>
    /// Replaces the data of the query under <paramref name="key"/> with <paramref name="updater"/>(current data, or
    /// <see langword="default"/> when there is none). The updater may run more than once if a key changes
    /// concurrently, so keep it pure and fast and return a new value rather than mutating the current one.
    /// </summary>
    public QueryDataBatch Update<T>(QueryKey key, Func<T?, T> updater)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(updater);
        Add(key, updater, entry: null);
        return this;
    }

    /// <summary>Replaces the data of the query described by <paramref name="query"/>.</summary>
    public QueryDataBatch Set<T>(QueryDefinition<T> query, T data)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Set(query.Key, data);
    }

    /// <summary>Updates the data of the query described by <paramref name="query"/> (see <see cref="Update{T}(QueryKey, Func{T, T})"/>).</summary>
    public QueryDataBatch Update<T>(QueryDefinition<T> query, Func<T?, T> updater)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Update(query.Key, updater);
    }

    internal void Add<T>(QueryKey key, Func<T?, T> updater, QueryEntry<T>? entry)
    {
        foreach (var write in _writes)
        {
            if (write.Key == key)
            {
                if (write is not BatchWrite<T> typed)
                {
                    throw new InvalidOperationException(
                        $"Query {key} is written with two different data types in one transaction ({write.DataTypeName} and {TypeKeyName<T>.Value}).");
                }

                typed.Updates.Add(updater);
                return;
            }
        }

        _writes.Add(new BatchWrite<T>(key, updater, entry));
    }
}

/// <summary>
/// The outcome of an atomic multi-query write: which queries were written, and a way to undo the writes.
/// </summary>
public sealed class QueryDataCommit
{
    private readonly IReadOnlyList<BatchWrite> _writes;
    private int _rolledBack;

    internal QueryDataCommit(IReadOnlyList<BatchWrite> writes)
    {
        _writes = writes;
        Keys = writes.Select(w => w.Key).ToArray();
    }

    /// <summary>Gets the keys of the queries that were written.</summary>
    public IReadOnlyList<QueryKey> Keys { get; }

    /// <summary>
    /// Atomically restores every written query to its exact state before the commit (data, timestamps, status and
    /// staleness) - except queries that changed again since (another write or a refetch landed on top), which are
    /// left alone so that newer write is not lost. Only the first call has an effect.
    /// </summary>
    /// <returns>The keys that were not restored because they changed after the commit (refetch or invalidate them to reconcile).</returns>
    public IReadOnlyList<QueryKey> Rollback()
    {
        if (Interlocked.Exchange(ref _rolledBack, 1) != 0)
        {
            return [];
        }

        var conflicts = new List<QueryKey>();
        var rebases = new List<(BatchWrite Write, object Plan)>();
        QueryDataTransaction.WithLocks(_writes, () =>
        {
            foreach (var write in _writes)
            {
                switch (write.RollbackLocked(out var plan))
                {
                    case RollbackOutcome.Conflict:
                        conflicts.Add(write.Key);
                        break;
                    case RollbackOutcome.NeedsRebase:
                        rebases.Add((write, plan!));
                        break;
                }
            }
        });

        // Optimistic writes of other pending mutations sit above some of ours: re-apply them on our snapshots (their
        // updaters run outside every lock). A rebase that races with another write falls back to a refetch.
        foreach (var (write, plan) in rebases)
        {
            if (!write.TryRebase(plan))
            {
                conflicts.Add(write.Key);
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Marks the writes as accepted by the server (optimistic commits only): they are never rolled back, and remain
    /// re-appliable while optimistic writes of still-pending mutations sit below them.
    /// </summary>
    internal void Complete()
    {
        foreach (var write in _writes)
        {
            write.Commit();
        }
    }
}

/// <summary>Atomic, multi-query cache writes.</summary>
public static class QueryDataTransactionExtensions
{
    /// <summary>
    /// Applies every write collected by <paramref name="build"/> atomically: observers and readers see either none
    /// or all of them, and no concurrent write to an involved key is lost (updaters are re-run against newer data
    /// when a key changes while they compute). If an updater throws, nothing is written and the exception
    /// propagates. Like <c>SetQueryData</c>, written data is marked fresh and an in-flight fetch for a key will
    /// overwrite it when it completes - cancel those fetches first for optimistic writes.
    /// </summary>
    /// <example>
    /// <code>
    /// var commit = client.SetQueriesData(tx => tx
    ///     .Update&lt;List&lt;Todo&gt;&gt;(TodoKeys.List, todos => [.. todos ?? [], added])
    ///     .Set(TodoKeys.ById(added.Id), added)
    ///     .Update&lt;int&gt;(TodoKeys.Count, count => count + 1));
    /// // later, if the server rejected the change:
    /// commit.Rollback();
    /// </code>
    /// </example>
    public static QueryDataCommit SetQueriesData(this IQueryClient client, Action<QueryDataBatch> build)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(build);
        var batch = new QueryDataBatch();
        build(batch);
        return QueryDataTransaction.Commit(client.Cache, batch.Writes);
    }

    /// <summary>
    /// Atomically applies <paramref name="updater"/> to every query matching <paramref name="filter"/> whose data is
    /// of type <typeparamref name="T"/> and that currently holds data (for example to patch one item in every cached
    /// list). Queries of other types, and queries without data, are skipped.
    /// </summary>
    public static QueryDataCommit SetQueriesData<T>(this IQueryClient client, QueryFilter filter, Func<T?, T> updater)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(updater);
        var batch = new QueryDataBatch();
        foreach (var entry in client.Cache.FindEntries(filter))
        {
            if (entry is QueryEntry<T> typed && typed.CurrentState.HasData)
            {
                batch.Add(typed.Key, updater, typed);
            }
        }

        return QueryDataTransaction.Commit(client.Cache, batch.Writes);
    }
}

/// <summary>The commit protocol behind <see cref="QueryDataTransactionExtensions"/>.</summary>
internal static class QueryDataTransaction
{
    public static QueryDataCommit Commit(QueryCache cache, IReadOnlyList<BatchWrite> writes, bool optimistic = false)
    {
        var live = writes.ToList();
        foreach (var write in live)
        {
            write.Optimistic = optimistic;
        }

        while (true)
        {
            // Resolve entries and compute every new value outside any lock (updaters are user code).
            live.RemoveAll(w => !w.Resolve(cache));
            foreach (var write in live)
            {
                write.Prepare();
            }

            var committed = false;
            WithLocks(live, () =>
            {
                // All-or-nothing: if any entry moved on (or was removed) since it was read, recompute.
                foreach (var write in live)
                {
                    if (!write.IsCurrentLocked())
                    {
                        return;
                    }
                }

                foreach (var write in live)
                {
                    write.ApplyLocked();
                }

                committed = true;
            });

            if (committed)
            {
                return new QueryDataCommit(live.Where(w => !w.Skipped).ToList());
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> holding every involved entry lock, taken in global id order (the only place
    /// that holds more than one entry lock, so the order rules out deadlocks), then delivers the queued notifications.
    /// </summary>
    public static void WithLocks(IReadOnlyList<BatchWrite> writes, Action action)
    {
        var ordered = writes.OrderBy(w => w.EntryId).ToArray();
        var taken = 0;
        try
        {
            foreach (var write in ordered)
            {
                Monitor.Enter(write.Gate);
                taken++;
            }

            action();
        }
        finally
        {
            for (var i = taken - 1; i >= 0; i--)
            {
                Monitor.Exit(ordered[i].Gate);
            }
        }

        foreach (var write in ordered)
        {
            write.FlushNotifications();
        }
    }
}

/// <summary>One key's pending write inside a transaction, and afterwards its rollback record.</summary>
internal abstract class BatchWrite
{
    public abstract QueryKey Key { get; }

    public abstract string DataTypeName { get; }

    public abstract long EntryId { get; }

    public abstract object Gate { get; }

    /// <summary>Binds the write to the live entry. Returns <see langword="false"/> to drop a write bound to a removed entry.</summary>
    public abstract bool Resolve(QueryCache cache);

    /// <summary>Whether the write was dropped because its (filter-matched) query no longer holds data.</summary>
    public abstract bool Skipped { get; }

    public abstract void Prepare();

    public abstract bool IsCurrentLocked();

    public abstract void ApplyLocked();

    public abstract bool RestoreLocked();

    public abstract void FlushNotifications();

    /// <summary>Whether this write belongs to a mutation's optimistic update (it then joins the entry's optimistic layers).</summary>
    public bool Optimistic { get; set; }

    /// <summary>Rolls back the write (the caller holds every involved lock).</summary>
    public abstract RollbackOutcome RollbackLocked(out object? rebasePlan);

    /// <summary>Executes a rebase planned by <see cref="RollbackLocked"/> (no locks held).</summary>
    public abstract bool TryRebase(object plan);

    /// <summary>Marks the write as accepted by the server.</summary>
    public abstract void Commit();
}

internal enum RollbackOutcome
{
    Restored,
    NeedsRebase,
    Conflict,
}

internal sealed class BatchWrite<T>(QueryKey key, Func<T?, T> updater, QueryEntry<T>? entry) : BatchWrite
{
    private readonly bool _pinned = entry is not null;
    private QueryEntry<T>? _entry = entry;
    private QueryState<T> _previous = QueryState<T>.Idle;
    private bool _previousInvalidated;
    private long _observedVersion = -1;
    private T _candidate = default!;
    private bool _skipped;

    public List<Func<T?, T>> Updates { get; } = [updater];

    /// <summary>The state this write replaced (rewritten when a rebase changes what lies below it).</summary>
    public QueryState<T> PreviousState
    {
        get => _previous;
        set => _previous = value;
    }

    public bool PreviousInvalidated
    {
        get => _previousInvalidated;
        set => _previousInvalidated = value;
    }

    public long ObservedVersion => _observedVersion;

    /// <summary>The entry version this write produced (or, after a rebase, the version of the rebased result).</summary>
    public long WrittenVersion { get; set; } = -1;

    public bool Committed { get; set; }

    /// <summary>Re-runs this write's updaters on another base value (a rebase).</summary>
    public T Reapply(T? value)
    {
        foreach (var update in Updates)
        {
            value = update(value);
        }

        return value!;
    }

    public override bool Skipped => _skipped;

    public override QueryKey Key { get; } = key;

    public override string DataTypeName => TypeKeyName<T>.Value;

    public override long EntryId => _entry!.Id;

    public override object Gate => _entry!.Gate;

    public override bool Resolve(QueryCache cache)
    {
        if (_pinned)
        {
            return !_entry!.IsRemoved;
        }

        if (_entry is null || _entry.IsRemoved)
        {
            _entry = cache.GetOrCreate<T>(Key);
        }

        return true;
    }

    public override void Prepare()
    {
        if (_entry!.SnapshotForWrite() is not { } snapshot)
        {
            _observedVersion = -1; // removed: IsCurrentLocked fails and the next round re-resolves
            return;
        }

        (_previous, _previousInvalidated, _observedVersion) = snapshot;

        // A write chosen by filter (only queries holding data) must not resurrect a query that was reset or emptied
        // after it matched; the version check still guards against it changing again before the commit.
        _skipped = _pinned && !_previous.HasData;
        if (_skipped)
        {
            return;
        }

        var value = _previous.HasData ? _previous.Data : default;
        foreach (var update in Updates)
        {
            value = update(value);
        }

        _candidate = value!;
    }

    public override bool IsCurrentLocked() => _observedVersion >= 0 && _entry!.IsAtVersionLocked(_observedVersion);

    public override void ApplyLocked()
    {
        if (!_skipped)
        {
            WrittenVersion = _entry!.WriteDataLocked(_candidate);
            if (Optimistic)
            {
                _entry.AddOptimisticLayerLocked(this);
            }
        }
    }

    public override bool RestoreLocked() => _skipped || _entry!.TryRestoreLocked(_previous, _previousInvalidated, WrittenVersion);

    public override RollbackOutcome RollbackLocked(out object? rebasePlan)
    {
        rebasePlan = null;
        if (_skipped)
        {
            return RollbackOutcome.Restored;
        }

        switch (_entry!.RollbackLayerLocked(this, out var plan))
        {
            case QueryEntry<T>.LayerRollback.Restored:
                return RollbackOutcome.Restored;
            case QueryEntry<T>.LayerRollback.NeedsRebase:
                rebasePlan = plan;
                return RollbackOutcome.NeedsRebase;
            case QueryEntry<T>.LayerRollback.Conflict:
                return RollbackOutcome.Conflict;
            default:
                return RestoreLocked() ? RollbackOutcome.Restored : RollbackOutcome.Conflict;
        }
    }

    public override bool TryRebase(object plan) => _entry!.TryRebase((QueryEntry<T>.RebasePlan)plan);

    public override void Commit()
    {
        if (!_skipped)
        {
            _entry!.CommitLayer(this);
        }
    }

    public override void FlushNotifications() => _entry!.FlushNotifications();
}
