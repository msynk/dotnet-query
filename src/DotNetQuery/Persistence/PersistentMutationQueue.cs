using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DotNetQuery.Persistence;

/// <summary>A mutation waiting in a <see cref="PersistentMutationQueue"/>.</summary>
/// <param name="Id">The stable identifier of the queued entry (also its storage order).</param>
/// <param name="MutationKey">The registered handler key the mutation replays through.</param>
/// <param name="EnqueuedAt">When the mutation was queued.</param>
public sealed record PendingMutation(string Id, string MutationKey, DateTimeOffset EnqueuedAt);

/// <summary>A mutation that failed permanently and was moved to the dead-letter queue (see <see cref="PersistentMutationQueueOptions.UseDeadLetterQueue"/>).</summary>
/// <param name="Mutation">The failed mutation (its id stays stable, so it can serve as an idempotency key).</param>
/// <param name="Error">The message of the final error.</param>
/// <param name="FailedAt">When it was dead-lettered.</param>
public sealed record DeadLetterMutation(PendingMutation Mutation, string Error, DateTimeOffset FailedAt);

/// <summary>What a <see cref="PersistentMutationQueueOptions.OnReplayFailure"/> callback decides for a failed replay.</summary>
public enum ReplayDecision
{
    /// <summary>Apply <c>MaxAttempts</c>, <c>RetryDelay</c> and <c>ShouldRetry</c> as usual.</summary>
    Default,

    /// <summary>Retry now (still bounded by <c>MaxAttempts</c>).</summary>
    Retry,

    /// <summary>Fail it permanently: dead-letter it (when enabled) or drop it, and continue with the next one.</summary>
    Fail,

    /// <summary>Drop it without dead-lettering, and continue with the next one.</summary>
    Drop,

    /// <summary>Keep it at the head and pause the queue (see <c>PersistentMutationQueue.ResumeAsync</c>), for example until the user re-authenticates.</summary>
    Pause,
}

/// <summary>The error reported for a queued mutation that was not replayed because it outlived <see cref="PersistentMutationQueueOptions.MaxAge"/>.</summary>
public sealed class MutationExpiredException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MutationExpiredException()
        : this("The queued mutation expired before it could be replayed.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public MutationExpiredException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public MutationExpiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Options for a <see cref="PersistentMutationQueue"/>.</summary>
public sealed record PersistentMutationQueueOptions
{
    /// <summary>Serialization options for mutation variables (reflection-based handlers). Defaults to <see cref="JsonSerializerDefaults.Web"/>.</summary>
    public JsonSerializerOptions? JsonOptions { get; init; }

    /// <summary>The clock used to timestamp queued mutations. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// How many times a replayed mutation is attempted while online before it fails permanently. Defaults to 1 (no
    /// retry). Retries keep the queue order: later mutations wait.
    /// </summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>The delay before each retry. Defaults to none.</summary>
    public RetryDelayStrategy? RetryDelay { get; init; }

    /// <summary>
    /// Decides whether a failure is worth retrying (for example <see cref="HttpRetryPolicy.ShouldRetry"/>, so a 400 or
    /// 409 fails at once while a 503 is retried). <see langword="null"/> retries every failure within <see cref="MaxAttempts"/>.
    /// </summary>
    public RetryPredicate? ShouldRetry { get; init; }

    /// <summary>
    /// Queued mutations older than this are not replayed (stale writes can be worse than none); they fail with a
    /// <see cref="MutationExpiredException"/>. <see langword="null"/> (the default) never expires them.
    /// </summary>
    public TimeSpan? MaxAge { get; init; }

    /// <summary>
    /// Decides what happens to a replayed mutation that failed while online - for example to resolve a conflict
    /// (<see cref="HttpRetryPolicy.IsConflict"/>) by dead-lettering it for the user, or to pause the queue on a 401 until
    /// the user signs in again. Receives the mutation, the error and the attempt number.
    /// <see cref="ReplayDecision.Default"/> applies the retry options.
    /// </summary>
    public Func<PendingMutation, Exception, int, ReplayDecision>? OnReplayFailure { get; init; }

    /// <summary>
    /// When <see langword="true"/>, mutations that fail permanently (or expire) are moved to a durable dead-letter
    /// queue (<see cref="PersistentMutationQueue.DeadLetters"/>) for inspection, retry or discard, instead of being
    /// dropped. Defaults to <see langword="false"/>.
    /// </summary>
    public bool UseDeadLetterQueue { get; init; }

    /// <summary>
    /// Coordinates queues in several browser tabs or processes that share one store. With a lock, a queue replays
    /// only while it holds the lock, and reloads the store first - so it also replays mutations the other instances
    /// queued, and skips the ones they already replayed. Without one (the default), the store must belong to this
    /// queue alone: two instances restoring the same store would each replay every queued mutation.
    /// See <see cref="FileMutationQueueLock"/> and the Blazor package's <c>WebLocksMutationQueueLock</c>.
    /// </summary>
    public IMutationQueueLock? ReplayLock { get; init; }
}

/// <summary>
/// A durable offline mutation queue: mutations are written to an <see cref="IQueryPersister"/> before they
/// run, replayed in order when connectivity returns, and survive app restarts (call
/// <see cref="RestoreAsync"/> at startup).
/// </summary>
/// <remarks>
/// <para>
/// Because queued work must outlive the process, mutations are described as data - a handler key plus
/// serializable variables - instead of closures. Register a handler per mutation kind with
/// <see cref="RegisterHandler{TVariables}(string, Func{TVariables, CancellationToken, Task}, JsonTypeInfo{TVariables})"/>
/// before calling <see cref="RestoreAsync"/> or <see cref="EnqueueAsync{TVariables}"/>.
/// </para>
/// <para>
/// Every mutation is persisted first and removed only after it completes, so a crash mid-request replays
/// it on the next start - make handlers idempotent (for example with a client-generated id). A mutation
/// that fails while online is dropped and reported through <see cref="MutationFailed"/>; one that fails
/// because connectivity dropped stays at the head of the queue.
/// </para>
/// <para>
/// Use a store dedicated to the queue (for example its own file-system directory, IndexedDB store, or
/// SQLite database) so <c>ClearAsync</c> on the query cache store cannot drop queued writes. When several
/// instances share the store (browser tabs, app windows in separate processes), set
/// <see cref="PersistentMutationQueueOptions.ReplayLock"/> so each queued mutation is replayed by one of them.
/// </para>
/// </remarks>
public sealed class PersistentMutationQueue : IDisposable
{
    private const string KeyPrefix = "dnq-mutation:";
    private const string DeadPrefix = "dnq-dead:";
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);

    private readonly IQueryPersister _store;
    private readonly IConnectivityProvider _connectivity;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Handler> _handlers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly List<Entry> _pending = [];
    private readonly List<DeadEntry> _dead = [];
    private readonly PersistentMutationQueueOptions _options;
    private Entry? _running;
    private bool _paused;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly AsyncLocal<ReplayScope?> _replayScope = new();
    private readonly Action<bool> _connectivityHandler;
    private readonly string _instance = Guid.NewGuid().ToString("N")[..8];
    private long _sequence;
    private int _disposed;

    /// <summary>Creates a queue over a store and a connectivity provider.</summary>
    public PersistentMutationQueue(IQueryPersister store, IConnectivityProvider connectivity, PersistentMutationQueueOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
        _options = options ?? new PersistentMutationQueueOptions();
        _jsonOptions = options?.JsonOptions ?? DefaultJson;
        _timeProvider = options?.TimeProvider ?? TimeProvider.System;
        _connectivityHandler = online =>
        {
            if (online)
            {
                _ = FlushAsync();
            }
        };
        _connectivity.ConnectivityChanged += _connectivityHandler;
    }

    /// <summary>Raised when a replayed mutation fails while online and is dropped (or has no registered handler).</summary>
    public event Action<PendingMutation, Exception>? MutationFailed;

    /// <summary>Raised whenever the set of pending mutations changes.</summary>
    public event Action? PendingChanged;

    /// <summary>Gets the number of mutations waiting to be replayed.</summary>
    public int PendingCount
    {
        get { lock (_gate) { return _pending.Count; } }
    }

    /// <summary>Gets a snapshot of the mutations waiting to be replayed, oldest first.</summary>
    public IReadOnlyList<PendingMutation> Pending
    {
        get { lock (_gate) { return _pending.Select(e => e.Info).ToArray(); } }
    }

    /// <summary>Gets the mutations that failed permanently, oldest first (empty unless <see cref="PersistentMutationQueueOptions.UseDeadLetterQueue"/> is set).</summary>
    public IReadOnlyList<DeadLetterMutation> DeadLetters
    {
        get { lock (_gate) { return _dead.Select(e => e.Info).ToArray(); } }
    }

    /// <summary>Gets a value indicating whether replay is paused (see <see cref="Pause"/>).</summary>
    public bool IsPaused
    {
        get { lock (_gate) { return _paused; } }
    }

    /// <summary>
    /// Stops replaying (mutations keep being stored) - for example until the user has signed in again, so queued writes
    /// are not sent with missing or wrong credentials. A replay already running finishes its current mutation.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
        }
    }

    /// <summary>Resumes replaying and, when online, flushes the queue.</summary>
    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _paused = false;
        }

        return _connectivity.IsOnline ? FlushAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <summary>
    /// Removes a queued mutation that has not started replaying (for example one the user cancelled).
    /// </summary>
    /// <returns><see langword="false"/> if no such mutation is queued, or it is being replayed right now.</returns>
    public async Task<bool> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        // Serialized with enqueue and restore, so a restore in progress cannot bring the mutation back.
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Entry? entry;
            lock (_gate)
            {
                entry = _pending.FirstOrDefault(e => e.Info.Id == id);
                if (entry is null || ReferenceEquals(entry, _running))
                {
                    return false;
                }

                _pending.Remove(entry);
            }

            await _store.RemoveAsync(KeyPrefix + id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _enqueueGate.Release();
        }

        RaisePendingChanged();
        return true;
    }

    /// <summary>
    /// Drops every queued mutation that is not being replayed right now, and every dead letter - in memory and in the
    /// store, including entries a previous session left there that were not restored yet - for example when the
    /// signed-in user changes and the previous user's writes must not be sent.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? running;
            lock (_gate)
            {
                running = _running?.Info.Id;
                _pending.RemoveAll(e => !ReferenceEquals(e, _running));
                _dead.Clear();
            }

            foreach (var stored in await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var isQueued = stored.Key.StartsWith(KeyPrefix, StringComparison.Ordinal);
                if ((isQueued && stored.Key[KeyPrefix.Length..] != running) || stored.Key.StartsWith(DeadPrefix, StringComparison.Ordinal))
                {
                    await _store.RemoveAsync(stored.Key, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _enqueueGate.Release();
        }

        RaisePendingChanged();
    }

    /// <summary>
    /// Moves a dead letter back to the end of the queue (keeping its id, which stays a valid idempotency key, and
    /// restarting its <see cref="PersistentMutationQueueOptions.MaxAge"/>) and, when online, flushes.
    /// </summary>
    /// <returns><see langword="false"/> if no such dead letter exists.</returns>
    public async Task<bool> RetryDeadLetterAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeadEntry? dead;
            lock (_gate)
            {
                dead = _dead.FirstOrDefault(e => e.Info.Mutation.Id == id);
            }

            if (dead is null)
            {
                return false;
            }

            // Store first, then move in memory: a failed save leaves the dead letter where it was. The id stays, so
            // the entry carries a new replay position, persisted with it: a restore sorts by it, not by the id.
            var info = dead.Info.Mutation with { EnqueuedAt = _timeProvider.GetUtcNow() };
            var order = NextOrder(info.EnqueuedAt);
            var payload = WriteEnvelope(info.MutationKey, ReadVariables(dead.Payload), order: order);
            await _store.SaveAsync(new PersistedQuery(KeyPrefix + info.Id, payload, info.EnqueuedAt), cancellationToken).ConfigureAwait(false);
            await _store.RemoveAsync(DeadPrefix + info.Id, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _dead.Remove(dead);
                _pending.Add(new Entry(info, payload, order));
            }
        }
        finally
        {
            _enqueueGate.Release();
        }

        RaisePendingChanged();
        if (_connectivity.IsOnline)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Permanently drops a dead letter.</summary>
    /// <returns><see langword="false"/> if no such dead letter exists.</returns>
    public async Task<bool> DiscardDeadLetterAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_dead.RemoveAll(e => e.Info.Mutation.Id == id) == 0)
                {
                    return false;
                }
            }

            await _store.RemoveAsync(DeadPrefix + id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _enqueueGate.Release();
        }

        RaisePendingChanged();
        return true;
    }

    /// <summary>
    /// Registers the handler that runs mutations queued under <paramref name="mutationKey"/>, with variables
    /// serialized by reflection-based JSON. In trimmed or NativeAOT apps use the <see cref="JsonTypeInfo{T}"/> overload.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization of TVariables may require types that cannot be statically analyzed. Use the JsonTypeInfo<TVariables> overload.")]
    [RequiresDynamicCode("JSON serialization of TVariables may require runtime code generation. Use the JsonTypeInfo<TVariables> overload.")]
    public PersistentMutationQueue RegisterHandler<TVariables>(string mutationKey, Func<TVariables, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterHandler<TVariables>(mutationKey, (variables, _, ct) => handler(variables, ct));
    }

    /// <summary>
    /// Registers a handler that also receives the queued <see cref="PendingMutation"/> - use its stable
    /// <see cref="PendingMutation.Id"/> as an idempotency key (for example an <c>Idempotency-Key</c> header) so the
    /// server can recognize a replay of a write it already applied. Reflection-based JSON.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization of TVariables may require types that cannot be statically analyzed. Use the JsonTypeInfo<TVariables> overload.")]
    [RequiresDynamicCode("JSON serialization of TVariables may require runtime code generation. Use the JsonTypeInfo<TVariables> overload.")]
    public PersistentMutationQueue RegisterHandler<TVariables>(string mutationKey, Func<TVariables, PendingMutation, CancellationToken, Task> handler)
    {
        var options = _jsonOptions;
        return Add(
            mutationKey,
            handler,
            v => JsonSerializer.Serialize(v, options),
            json => JsonSerializer.Deserialize<TVariables>(json, options)!);
    }

    /// <summary>
    /// Registers the handler that runs mutations queued under <paramref name="mutationKey"/>, with variables
    /// serialized by <paramref name="typeInfo"/>. Trim- and NativeAOT-safe.
    /// </summary>
    public PersistentMutationQueue RegisterHandler<TVariables>(string mutationKey, Func<TVariables, CancellationToken, Task> handler, JsonTypeInfo<TVariables> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterHandler(mutationKey, (TVariables variables, PendingMutation _, CancellationToken ct) => handler(variables, ct), typeInfo);
    }

    /// <summary>
    /// Registers a handler that also receives the queued <see cref="PendingMutation"/> (whose stable
    /// <see cref="PendingMutation.Id"/> is an idempotency key), with variables serialized by <paramref name="typeInfo"/>.
    /// Trim- and NativeAOT-safe.
    /// </summary>
    public PersistentMutationQueue RegisterHandler<TVariables>(string mutationKey, Func<TVariables, PendingMutation, CancellationToken, Task> handler, JsonTypeInfo<TVariables> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Add(
            mutationKey,
            handler,
            v => JsonSerializer.Serialize(v, typeInfo),
            json => JsonSerializer.Deserialize(json, typeInfo)!);
    }

    private PersistentMutationQueue Add<TVariables>(
        string mutationKey,
        Func<TVariables, PendingMutation, CancellationToken, Task> handler,
        Func<TVariables, string> serialize,
        Func<string, TVariables> deserialize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationKey);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[mutationKey] = new Handler(
            typeof(TVariables),
            variables => serialize((TVariables)variables!),
            (json, mutation, ct) => handler(deserialize(json), mutation, ct));
        return this;
    }

    /// <summary>
    /// Durably queues a mutation, then (when online) replays the queue in order. Completes once the mutation
    /// is stored and, if online, the flush has run. Failures are reported via <see cref="MutationFailed"/>.
    /// </summary>
    /// <remarks>
    /// Called from a replay handler (for example to queue a follow-up write), it completes once the mutation is
    /// stored: the replay that runs the handler goes on to replay it.
    /// </remarks>
    public async Task EnqueueAsync<TVariables>(string mutationKey, TVariables variables, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationKey);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_handlers.TryGetValue(mutationKey, out var handler))
        {
            throw new InvalidOperationException($"No handler is registered for mutation '{mutationKey}'. Call RegisterHandler first.");
        }

        if (handler.VariablesType != typeof(TVariables))
        {
            throw new InvalidOperationException(
                $"Mutation '{mutationKey}' expects variables of type {handler.VariablesType.Name}, not {typeof(TVariables).Name}.");
        }

        var payload = WriteEnvelope(mutationKey, handler.Serialize(variables));

        // Serialize id allocation + durable write + insertion, so replay order is exactly call order even
        // when the store completes writes out of order.
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var enqueuedAt = _timeProvider.GetUtcNow();
            var id = NextOrder(enqueuedAt);
            await _store.SaveAsync(new PersistedQuery(KeyPrefix + id, payload, enqueuedAt), cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _pending.Add(new Entry(new PendingMutation(id, mutationKey, enqueuedAt), payload, id));
            }
        }
        finally
        {
            _enqueueGate.Release();
        }

        RaisePendingChanged();
        if (_connectivity.IsOnline)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads mutations persisted by a previous session into the queue (oldest first) and, when online,
    /// replays them. Register every handler first.
    /// </summary>
    /// <remarks>
    /// The pending list and the dead letters are reconciled with the store: entries another instance sharing the store
    /// added appear, and entries it replayed, removed or discarded disappear. Call it again to refresh
    /// <see cref="Pending"/> and <see cref="DeadLetters"/> in a tab or window that does not replay itself.
    /// </remarks>
    /// <returns>The number of mutations restored.</returns>
    public async Task<int> RestoreAsync(CancellationToken cancellationToken = default)
    {
        int restored;
        int removed;

        // Exclude concurrent enqueues and flushes while merging, so an entry being written or replayed right
        // now can neither be loaded twice nor resurrected after it completed. The flush gate is taken first (the
        // order a replay handler that enqueues takes them in), and not at all from a replay handler, whose flush
        // already holds it.
        var ownsFlush = !InReplayHandler;
        if (ownsFlush)
        {
            await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var stored = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
                (restored, removed) = Reconcile(stored);
            }
            finally
            {
                _enqueueGate.Release();
            }
        }
        finally
        {
            if (ownsFlush)
            {
                _flushGate.Release();
            }
        }

        if (restored > 0 || removed > 0)
        {
            RaisePendingChanged();
        }

        if (_connectivity.IsOnline)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return restored;
    }

    // Makes the in-memory lists match the store (callers hold _enqueueGate, and _flushGate or the replay running the
    // caller): stored entries this instance does not know are added, known entries the store no longer has (replayed,
    // removed or discarded by another instance sharing it) are dropped - except the one replaying right now.
    private (int Added, int Removed) Reconcile(IReadOnlyList<PersistedQuery> stored)
    {
        var restored = 0;
        var removed = 0;
        lock (_gate)
        {
            var storedKeys = stored.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            removed += _pending.RemoveAll(e => !ReferenceEquals(e, _running) && !storedKeys.Contains(KeyPrefix + e.Info.Id));
            removed += _dead.RemoveAll(e => !storedKeys.Contains(DeadPrefix + e.Info.Mutation.Id));

            var knownDead = _dead.Select(e => e.Info.Mutation.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in stored.Where(s => s.Key.StartsWith(DeadPrefix, StringComparison.Ordinal)).OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                var id = item.Key[DeadPrefix.Length..];
                if (!knownDead.Contains(id) && TryReadEnvelope(item.Payload, out var deadKey, out _))
                {
                    var error = TryReadError(item.Payload) ?? "unknown error";
                    _dead.Add(new DeadEntry(new DeadLetterMutation(new PendingMutation(id, deadKey, item.UpdatedAt), error, item.UpdatedAt), item.Payload));
                }
            }

            var known = _pending.Select(e => e.Info.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in stored.Where(s => s.Key.StartsWith(KeyPrefix, StringComparison.Ordinal)).OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                var id = item.Key[KeyPrefix.Length..];
                if (known.Contains(id) || !TryReadEnvelope(item.Payload, out var mutationKey, out _))
                {
                    continue;
                }

                _pending.Add(new Entry(new PendingMutation(id, mutationKey, item.UpdatedAt), item.Payload, TryReadOrder(item.Payload) ?? id));
                restored++;
            }

            _pending.Sort(static (a, b) => string.CompareOrdinal(a.Order, b.Order));
        }

        return (restored, removed);
    }

    // Under the replay lock: picks up what other instances sharing the store queued or replayed meanwhile.
    private async Task ReconcileWithStoreAsync(CancellationToken cancellationToken)
    {
        int added;
        int removed;
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (added, removed) = Reconcile(await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _enqueueGate.Release();
        }

        if (added > 0 || removed > 0)
        {
            RaisePendingChanged();
        }
    }

    /// <summary>
    /// Replays queued mutations in order while connectivity holds. Never throws for mutation failures;
    /// only cancellation propagates, leaving the remaining mutations queued.
    /// </summary>
    /// <remarks>
    /// Called from a replay handler, it returns at once: the replay running the handler continues the queue. With a
    /// <see cref="PersistentMutationQueueOptions.ReplayLock"/>, it first waits for the lock and reconciles the queue with
    /// the store.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || InReplayHandler)
        {
            return;
        }

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IAsyncDisposable? lease = null;
        try
        {
            if (_options.ReplayLock is { } replayLock)
            {
                if (!_connectivity.IsOnline)
                {
                    return;
                }

                lease = await replayLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
                await ReconcileWithStoreAsync(cancellationToken).ConfigureAwait(false);
            }

            await ReplayAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }

            if (lease is not null)
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A lock that cannot be released cleanly (a closed browser connection) is released by its owner.
                }
            }

            _flushGate.Release();
        }
    }

    // Callers hold _flushGate (and the replay lock, when configured).
    private async Task ReplayAsync(CancellationToken cancellationToken)
    {
        while (_connectivity.IsOnline)
        {
            Entry? head;
            lock (_gate)
            {
                head = !_paused && _pending.Count > 0 ? _pending[0] : null;
                _running = head;
            }

            if (head is null)
            {
                return;
            }

            if (!TryReadEnvelope(head.Payload, out var mutationKey, out var variablesJson) ||
                !_handlers.TryGetValue(mutationKey, out var handler))
            {
                // Keep the work (it may be replayable once the handler is registered) and preserve order.
                RaiseFailed(head.Info, new InvalidOperationException($"No handler is registered for mutation '{head.Info.MutationKey}'."));
                return;
            }

            if (_options.MaxAge is { } maxAge && _timeProvider.GetUtcNow() - head.Info.EnqueuedAt > maxAge)
            {
                await FailAsync(head, new MutationExpiredException($"Mutation '{head.Info.MutationKey}' expired after {maxAge} in the queue.")).ConfigureAwait(false);
                continue;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await RunHandlerAsync(handler, variablesJson, head.Info, cancellationToken).ConfigureAwait(false);
                    await RemoveAsync(head).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!_connectivity.IsOnline)
                    {
                        return; // dropped offline mid-flush: retry this one first on reconnect
                    }

                    var decision = Decide(head.Info, ex, attempt);
                    if (decision is ReplayDecision.Pause)
                    {
                        lock (_gate)
                        {
                            _paused = true;
                        }

                        RaiseFailed(head.Info, ex);
                        return;
                    }

                    if (decision is ReplayDecision.Drop)
                    {
                        await RemoveAsync(head).ConfigureAwait(false);
                        RaiseFailed(head.Info, ex);
                        break;
                    }

                    var retry = decision switch
                    {
                        ReplayDecision.Retry => attempt < Math.Max(1, _options.MaxAttempts),
                        ReplayDecision.Fail => false,
                        _ => attempt < Math.Max(1, _options.MaxAttempts) && ShouldRetry(attempt, ex),
                    };

                    if (retry && TryGetRetryDelay(attempt, ex, out var delay))
                    {
                        if (delay > TimeSpan.Zero)
                        {
                            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                        }

                        if (!_connectivity.IsOnline)
                        {
                            return;
                        }

                        continue;
                    }

                    await FailAsync(head, ex).ConfigureAwait(false);
                    break;
                }
            }
        }
    }

    // Runs a handler inside a replay scope, so the calls it makes back into the queue know that the flush awaiting it
    // holds _flushGate (waiting for it there would deadlock).
    private async Task RunHandlerAsync(Handler handler, string variablesJson, PendingMutation mutation, CancellationToken cancellationToken)
    {
        var scope = new ReplayScope();
        _replayScope.Value = scope;
        try
        {
            await handler.Run(variablesJson, mutation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            scope.Active = false;
        }
    }

    // Whether the caller runs inside a replay handler of this queue. The scope flows into work the handler starts, but
    // ends with the handler call, so work that outlives it takes the gates as usual.
    private bool InReplayHandler => _replayScope.Value is { Active: true };

    // Time first (replay order across instances sharing a store), then this instance's sequence, then an instance id so
    // two instances never produce the same id even within one clock tick (browsers coarsen timestamps to milliseconds).
    private string NextOrder(DateTimeOffset at) => $"{at.UtcTicks:D19}-{Interlocked.Increment(ref _sequence):D10}-{_instance}";

    // A throwing strategy ends the retries (the mutation fails with its own error); delays are clamped to what
    // Task.Delay accepts, so FlushAsync never throws because of the retry policy.
    private bool TryGetRetryDelay(int attempt, Exception error, out TimeSpan delay)
    {
        try
        {
            delay = _options.RetryDelay?.Invoke(attempt - 1, error) ?? TimeSpan.Zero;
        }
        catch (Exception)
        {
            delay = TimeSpan.Zero;
            return false;
        }

        if (delay > MaxRetryDelay)
        {
            delay = MaxRetryDelay;
        }

        return true;
    }

    private ReplayDecision Decide(PendingMutation mutation, Exception error, int attempt)
    {
        if (_options.OnReplayFailure is not { } decide)
        {
            return ReplayDecision.Default;
        }

        try
        {
            return decide(mutation, error, attempt);
        }
        catch (Exception)
        {
            return ReplayDecision.Default;
        }
    }

    private bool ShouldRetry(int failureCount, Exception error)
    {
        if (_options.ShouldRetry is not { } predicate)
        {
            return true;
        }

        try
        {
            return predicate(failureCount, error);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // A permanent failure: dead-letter the mutation (durably, before it leaves the queue) or drop it.
    private async Task FailAsync(Entry entry, Exception error)
    {
        if (_options.UseDeadLetterQueue)
        {
            var failedAt = _timeProvider.GetUtcNow();
            var payload = WriteEnvelope(entry.Info.MutationKey, ReadVariables(entry.Payload), error.Message);
            try
            {
                await _store.SaveAsync(new PersistedQuery(DeadPrefix + entry.Info.Id, payload, failedAt), CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _dead.Add(new DeadEntry(new DeadLetterMutation(entry.Info, error.Message, failedAt), payload));
                }
            }
            catch (Exception storeError)
            {
                RaiseFailed(entry.Info, new InvalidOperationException(
                    $"Mutation '{entry.Info.MutationKey}' failed and could not be dead-lettered; it was dropped.", storeError));
            }
        }

        await RemoveAsync(entry).ConfigureAwait(false);
        RaiseFailed(entry.Info, error);
    }

    private async Task RemoveAsync(Entry entry)
    {
        try
        {
            await _store.RemoveAsync(KeyPrefix + entry.Info.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The mutation already ran; drop it from this session regardless. It may replay after a restart
            // (handlers must be idempotent), so surface the storage failure.
            RaiseFailed(entry.Info, new InvalidOperationException(
                $"Mutation '{entry.Info.MutationKey}' ran, but could not be removed from the store; it may replay after a restart.", ex));
        }

        lock (_gate)
        {
            _pending.Remove(entry);
        }

        RaisePendingChanged();
    }

    private static string WriteEnvelope(string mutationKey, string variablesJson, string? error = null, string? order = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mutationKey", mutationKey);
            writer.WriteString("variables", variablesJson);
            if (error is not null)
            {
                writer.WriteString("error", error);
            }

            if (order is not null)
            {
                writer.WriteString("order", order);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string ReadVariables(string payload) => TryReadEnvelope(payload, out _, out var variables) ? variables : "null";

    private static string? TryReadError(string payload) => TryReadString(payload, "error");

    // The replay position of a retried dead letter; other entries replay in id order.
    private static string? TryReadOrder(string payload) => TryReadString(payload, "order");

    private static string? TryReadString(string payload, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadEnvelope(string payload, out string mutationKey, out string variablesJson)
    {
        mutationKey = variablesJson = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("mutationKey", out var key) && key.GetString() is { } k &&
                document.RootElement.TryGetProperty("variables", out var variables) && variables.GetString() is { } v)
            {
                mutationKey = k;
                variablesJson = v;
                return true;
            }
        }
        catch (JsonException)
        {
            // Corrupt entry: treated as unreadable.
        }

        return false;
    }

    private void RaisePendingChanged()
    {
        try
        {
            PendingChanged?.Invoke();
        }
        catch (Exception)
        {
            // Subscriber errors must not break the queue.
        }
    }

    private void RaiseFailed(PendingMutation mutation, Exception error)
    {
        try
        {
            MutationFailed?.Invoke(mutation, error);
        }
        catch (Exception)
        {
            // Subscriber errors must not break the queue.
        }
    }

    /// <summary>Stops listening for connectivity changes. Queued mutations remain in the store.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _connectivity.ConnectivityChanged -= _connectivityHandler;
        }
    }

    private sealed record Handler(Type VariablesType, Func<object?, string> Serialize, Func<string, PendingMutation, CancellationToken, Task> Run);

    private sealed record Entry(PendingMutation Info, string Payload, string Order);

    private sealed record DeadEntry(DeadLetterMutation Info, string Payload);

    private sealed class ReplayScope
    {
        public volatile bool Active = true;
    }
}
