namespace DotNetQuery;

/// <summary>
/// Tracks every mutation run of a client - pending ones and, for <see cref="QueryClientOptions.MutationCacheTime"/>,
/// settled ones - so any part of an app can ask "is anything saving?", list failed writes, or react to every
/// mutation globally (for example a toast on any error) without holding the <see cref="Mutation{TVariables, TData}"/>
/// instances. Also coordinates <see cref="MutationOptions{TVariables, TData}.Scope"/> serialization.
/// </summary>
public sealed class MutationCache
{
    private readonly object _gate = new();
    private readonly Dictionary<long, MutationRun> _runs = [];
    private readonly Dictionary<string, Task> _scopeTails = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;
    private long _ids;

    internal MutationCache(TimeProvider timeProvider, TimeSpan retention)
    {
        _timeProvider = timeProvider;
        _retention = retention;
    }

    /// <summary>
    /// Raised when a run is added, changes state, or is removed. Handlers run synchronously on the thread that
    /// made the change and must be fast; exceptions they throw are swallowed.
    /// </summary>
    public event Action<MutationCacheChange>? Changed;

    /// <summary>Gets the number of tracked runs (pending and retained settled ones).</summary>
    public int Count
    {
        get { lock (_gate) { return _runs.Count; } }
    }

    /// <summary>Describes every tracked run matching <paramref name="filter"/> (all when <see langword="null"/>), oldest first.</summary>
    public IReadOnlyList<MutationDescriptor> FindAll(MutationFilter? filter = null)
    {
        MutationRun[] runs;
        lock (_gate)
        {
            runs = [.. _runs.Values];
        }

        var matched = new List<MutationDescriptor>(runs.Length);
        foreach (var run in runs.OrderBy(r => r.Id))
        {
            var descriptor = run.Describe();
            if (filter is null || filter.Matches(descriptor))
            {
                matched.Add(descriptor);
            }
        }

        return matched;
    }

    /// <summary>Describes a single run, or returns <see langword="null"/> if it is not (or no longer) tracked.</summary>
    public MutationDescriptor? Find(long mutationId)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(mutationId, out var run) ? run.Describe() : null;
        }
    }

    /// <summary>Returns how many runs matching <paramref name="filter"/> are pending - for a global "saving…" indicator.</summary>
    public int CountPending(MutationFilter? filter = null) =>
        FindAll(filter is null ? new MutationFilter { Status = MutationStatus.Pending } : filter with { Status = MutationStatus.Pending }).Count;

    /// <summary>Removes settled runs older than the retention window. Runs automatically with query garbage collection.</summary>
    /// <returns>The number of runs removed.</returns>
    public int CollectGarbage()
    {
        var now = _timeProvider.GetUtcNow();
        var removed = new List<long>();
        lock (_gate)
        {
            foreach (var run in _runs.Values)
            {
                if (run.SettledAt is { } settled && now - settled >= _retention)
                {
                    removed.Add(run.Id);
                }
            }

            foreach (var id in removed)
            {
                _runs.Remove(id);
            }
        }

        foreach (var id in removed)
        {
            Raise(id, MutationCacheChangeKind.Removed);
        }

        return removed.Count;
    }

    /// <summary>
    /// Stops tracking every settled run. Pending runs stay tracked until they settle (they are still running).
    /// </summary>
    public void Clear()
    {
        var removed = new List<long>();
        lock (_gate)
        {
            foreach (var run in _runs.Values)
            {
                if (run.SettledAt is not null)
                {
                    removed.Add(run.Id);
                }
            }

            foreach (var id in removed)
            {
                _runs.Remove(id);
            }
        }

        foreach (var id in removed)
        {
            Raise(id, MutationCacheChangeKind.Removed);
        }
    }

    internal MutationRun Add(long id, QueryKey? mutationKey, string? scope, IReadOnlyDictionary<string, object?>? meta, object? variables, DateTimeOffset submittedAt)
    {
        var run = new MutationRun(this, id > 0 ? id : -Interlocked.Increment(ref _ids), mutationKey, scope, meta, variables, submittedAt);
        lock (_gate)
        {
            _runs[run.Id] = run;
        }

        Raise(run.Id, MutationCacheChangeKind.Added);
        return run;
    }

    internal void OnUpdated(MutationRun run)
    {
        if (_retention <= TimeSpan.Zero && run.SettledAt is not null)
        {
            lock (_gate)
            {
                _runs.Remove(run.Id);
            }

            Raise(run.Id, MutationCacheChangeKind.Updated);
            Raise(run.Id, MutationCacheChangeKind.Removed);
            return;
        }

        Raise(run.Id, MutationCacheChangeKind.Updated);
    }

    internal DateTimeOffset Now => _timeProvider.GetUtcNow();

    /// <summary>
    /// Waits until every earlier run in <paramref name="scope"/> has settled, then holds the scope until the returned
    /// lease is disposed. Runs enter in call order. <paramref name="onWait"/> runs (once) if the run has to wait.
    /// </summary>
    internal async Task<IDisposable> EnterScopeAsync(string scope, Action onWait, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            previous = _scopeTails.TryGetValue(scope, out var tail) ? tail : Task.CompletedTask;
            _scopeTails[scope] = done.Task;
        }

        var lease = new ScopeLease(this, scope, done);
        if (previous.IsCompleted)
        {
            return lease;
        }

        onWait();
        try
        {
            await previous.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Leaving the queue early must not let the next run overtake the ones still ahead of it: hand the
            // slot over only once everything before this run has settled.
            _ = previous.ContinueWith(
                static (_, state) => ((ScopeLease)state!).Dispose(),
                lease,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }

        return lease;
    }

    private void ReleaseScope(string scope, TaskCompletionSource done)
    {
        lock (_gate)
        {
            if (_scopeTails.TryGetValue(scope, out var tail) && ReferenceEquals(tail, done.Task))
            {
                _scopeTails.Remove(scope);
            }
        }

        done.TrySetResult();
    }

    private void Raise(long id, MutationCacheChangeKind kind)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        var change = new MutationCacheChange(id, kind);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<MutationCacheChange>)handler)(change);
            }
            catch (Exception)
            {
                // Isolate subscribers: a faulty listener must not break mutations.
            }
        }
    }

    private sealed class ScopeLease(MutationCache owner, string scope, TaskCompletionSource done) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseScope(scope, done);
            }
        }
    }
}

/// <summary>A tracked mutation run (internal, mutable); <see cref="Describe"/> produces public snapshots.</summary>
internal sealed class MutationRun(MutationCache owner, long id, QueryKey? mutationKey, string? scope, IReadOnlyDictionary<string, object?>? meta, object? variables, DateTimeOffset submittedAt)
{
    private readonly object _gate = new();
    private MutationStatus _status = MutationStatus.Pending;
    private object? _data;
    private Exception? _error;
    private int _failureCount;
    private bool _isPaused;
    private double? _progress;
    private DateTimeOffset? _settledAt;

    public long Id { get; } = id;

    public DateTimeOffset? SettledAt
    {
        get { lock (_gate) { return _settledAt; } }
    }

    public void Update(MutationStatus status, object? data, Exception? error, int failureCount, bool isPaused, double? progress = null)
    {
        lock (_gate)
        {
            if (_settledAt is not null)
            {
                return;
            }

            _status = status;
            _data = data;
            _error = error;
            _failureCount = failureCount;
            _isPaused = isPaused;
            _progress = progress;
            if (status is MutationStatus.Success or MutationStatus.Error)
            {
                _settledAt = owner.Now;
            }
        }

        owner.OnUpdated(this);
    }

    public MutationDescriptor Describe()
    {
        lock (_gate)
        {
            return new MutationDescriptor
            {
                MutationId = Id,
                MutationKey = mutationKey,
                Scope = scope,
                Meta = meta,
                Status = _status,
                IsPaused = _isPaused,
                Progress = _progress,
                Variables = variables,
                Data = _data,
                Error = _error,
                FailureCount = _failureCount,
                SubmittedAt = submittedAt,
                SettledAt = _settledAt,
            };
        }
    }
}

/// <summary>The kind of change reported by <see cref="MutationCache.Changed"/>.</summary>
public enum MutationCacheChangeKind
{
    /// <summary>A run started.</summary>
    Added,

    /// <summary>A run changed state (retry, paused/resumed, settled).</summary>
    Updated,

    /// <summary>A settled run stopped being tracked (retention elapsed or the cache was cleared).</summary>
    Removed,
}

/// <summary>Describes a single change to the <see cref="MutationCache"/>.</summary>
/// <param name="MutationId">The run's id (matches <see cref="MutationStartedEvent.MutationId"/>).</param>
/// <param name="Kind">What changed.</param>
public readonly record struct MutationCacheChange(long MutationId, MutationCacheChangeKind Kind);

/// <summary>A point-in-time, non-generic description of a mutation run.</summary>
public sealed record MutationDescriptor
{
    /// <summary>The client-unique id of the run.</summary>
    public required long MutationId { get; init; }

    /// <summary>The mutation's <see cref="MutationOptions{TVariables, TData}.MutationKey"/>, if any.</summary>
    public QueryKey? MutationKey { get; init; }

    /// <summary>The mutation's <see cref="MutationOptions{TVariables, TData}.Scope"/>, if any.</summary>
    public string? Scope { get; init; }

    /// <summary>The mutation's <see cref="MutationOptions{TVariables, TData}.Meta"/>, if any.</summary>
    public IReadOnlyDictionary<string, object?>? Meta { get; init; }

    /// <summary>The run's status.</summary>
    public required MutationStatus Status { get; init; }

    /// <summary>Whether the pending run is waiting (for earlier runs in its scope) rather than executing.</summary>
    public bool IsPaused { get; init; }

    /// <summary>The progress last reported by the run's mutation function, if it reports progress.</summary>
    public double? Progress { get; init; }

    /// <summary>The variables the run was invoked with (a live reference; treat as read-only).</summary>
    public object? Variables { get; init; }

    /// <summary>The data the run returned, once it succeeded.</summary>
    public object? Data { get; init; }

    /// <summary>The run's error: the last failed attempt while retrying, or the terminal error.</summary>
    public Exception? Error { get; init; }

    /// <summary>The number of failed attempts so far.</summary>
    public int FailureCount { get; init; }

    /// <summary>When the run was submitted.</summary>
    public required DateTimeOffset SubmittedAt { get; init; }

    /// <summary>When the run settled, or <see langword="null"/> while it is pending.</summary>
    public DateTimeOffset? SettledAt { get; init; }
}

/// <summary>Selects tracked mutation runs; unset criteria match everything.</summary>
public sealed record MutationFilter
{
    /// <summary>Matches runs whose mutation key starts with this key (or equals it when <see cref="Exact"/> is set).</summary>
    public QueryKey? MutationKey { get; init; }

    /// <summary>When <see langword="true"/>, <see cref="MutationKey"/> must match exactly.</summary>
    public bool Exact { get; init; }

    /// <summary>When set, matches only runs in this status.</summary>
    public MutationStatus? Status { get; init; }

    /// <summary>When set, matches only runs in this scope.</summary>
    public string? Scope { get; init; }

    /// <summary>An additional condition over the run's description.</summary>
    public Func<MutationDescriptor, bool>? Predicate { get; init; }

    /// <summary>Returns whether <paramref name="mutation"/> satisfies every criterion.</summary>
    public bool Matches(MutationDescriptor mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (MutationKey is { } key && (mutation.MutationKey is not { } actual || !(Exact ? key == actual : key.IsPrefixOf(actual))))
        {
            return false;
        }

        return (Status is not { } status || status == mutation.Status)
            && (Scope is null || string.Equals(Scope, mutation.Scope, StringComparison.Ordinal))
            && (Predicate is null || Predicate(mutation));
    }
}
