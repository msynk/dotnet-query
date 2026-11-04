using System.Diagnostics;
using System.Threading.Channels;

namespace DotNetQuery.Synchronization;

/// <summary>
/// Translates real-time server events into query cache operations: invalidations, typed incremental updates and entity
/// upserts and removals, with duplicate protection, ordering by entity version or stream sequence, coalesced refetches,
/// and recovery after a reconnect, a sequence gap or a queue overflow. Transport-agnostic: events arrive from an
/// <see cref="IQueryEventSource"/> (for example the SignalR source in <c>Msynk.DotNetQuery.SignalR</c>) or through
/// <see cref="ProcessAsync"/> and <see cref="Post"/>.
/// </summary>
/// <remarks>
/// <para>
/// Events are processed one at a time, in arrival order, on a background loop fed by a bounded queue, so the transport's
/// receive loop never waits for cache work (see <see cref="QueryEventSyncOptions.OverflowPolicy"/>). An event's id is
/// recorded as processed only after its mappings ran; a failed mapping invalidates the queries it targets instead.
/// </para>
/// <para>
/// Guarantees depend on the metadata the server provides (<see cref="QueryEventMetadata"/>): with event ids, redelivered
/// events are discarded within the deduplication window (in memory - not across process restarts); with entity versions
/// or stream sequences, older events cannot overwrite newer ones; without them, prefer invalidation mappings, which are
/// always correct. A recovery reconciles what may have been missed; replaying missed events requires a server that
/// retains them (<see cref="QuerySyncRecovery.Replay"/>).
/// </para>
/// <para>
/// The synchronizer never disposes the query client, and disposes its source only when constructed with
/// <c>ownsSource</c>. Dispose it with the client's owner (a DI scope, the app, a component).
/// </para>
/// </remarks>
public sealed class QueryEventSynchronizer : IAsyncDisposable, IDisposable
{
    private const int MaxRecentErrors = 20;
    private static readonly AsyncLocal<QueryEventSynchronizer?> s_processing = new();

    private readonly QueryClient? _diagnostics;
    private readonly QueryEventSyncOptions _options;
    private readonly TimeProvider _time;
    private readonly Channel<WorkItem> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly ExpiringIdSet _dedup;
    private readonly ExpiringIdSet _localOrigins;
    private readonly VersionTracker _versions;
    private readonly SequenceTracker _sequences = new();
    private readonly HashSet<QueryFilter> _pendingInvalidations = [];
    private readonly Queue<QuerySyncError> _errors = new();
    private readonly KeyValuePair<string, object?> _nameTag;
    private readonly Task _loop;

    private Dictionary<string, EventRegistration> _registrations = new(StringComparer.Ordinal);
    private QueryKey[] _coveredKeys = [];
    private IQueryEventSource? _source;
    private readonly bool _ownsSource;
    private readonly Action<QueryEventSourceStateChange>? _sourceHandler;
    private bool _everConnected;
    private string? _scope;
    private QuerySyncStatus _status;
    private int _sinceFlush;

    private int _recoveryRequested;
    private bool _recoveryRunning;
    private QuerySyncRecoveryReason _recoveryReason;
    private long _recoveryWatermark;
    private long _recoveryGeneration;

    private long _received;
    private long _applied;
    private long _duplicates;
    private long _stale;
    private long _filtered;
    private long _rejected;
    private long _echoes;
    private long _gapCount;
    private long _failed;
    private long _dropped;
    private long _recoveries;
    private long _recoveryFallbacks;
    private long _enqueued;
    private long _dequeued;
    private long _lastEventTicks;
    private long _lastSynchronizedTicks;
    private int _disposed;

    /// <summary>Creates a synchronizer for <paramref name="client"/>, optionally fed by <paramref name="source"/>.</summary>
    /// <param name="client">The query client whose cache the events update.</param>
    /// <param name="source">
    /// The transport delivering the events. Mappings subscribe to it as they are added; its connection state drives
    /// <see cref="Status"/> and recovery. <see langword="null"/> for events fed through <see cref="ProcessAsync"/> or
    /// <see cref="Post"/>.
    /// </param>
    /// <param name="options">Options; defaults when <see langword="null"/>.</param>
    /// <param name="ownsSource">Whether disposing the synchronizer disposes <paramref name="source"/>. Defaults to <see langword="false"/>.</param>
    public QueryEventSynchronizer(IQueryClient client, IQueryEventSource? source = null, QueryEventSyncOptions? options = null, bool ownsSource = false)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new QueryEventSyncOptions();
        _options.Validate();
        _diagnostics = client as QueryClient;
        _time = _options.TimeProvider ?? client.Cache.Runtime.TimeProvider;
        _dedup = new ExpiringIdSet(_options.DeduplicationCapacity, _options.DeduplicationWindow, _time);
        _localOrigins = new ExpiringIdSet(_options.DeduplicationCapacity, _options.DeduplicationWindow, _time);
        _versions = new VersionTracker(_options.VersionTrackingCapacity);
        _scope = _options.Scope;
        _nameTag = new("dotnetquery.sync.name", _options.Name);
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _status = source is null ? QuerySyncStatus.Synchronized : QuerySyncStatus.Disconnected;
        _loop = Task.Run(RunLoopAsync);

        if (source is not null)
        {
            _source = source;
            _ownsSource = ownsSource;
            _sourceHandler = OnSourceStateChanged;
            source.StateChanged += _sourceHandler;
            OnSourceStateChanged(new QueryEventSourceStateChange(source.State));
        }
    }

    /// <summary>Gets the query client.</summary>
    public IQueryClient Client { get; }

    /// <summary>Gets the synchronizer's <see cref="QueryEventSyncOptions.Name"/>.</summary>
    public string Name => _options.Name;

    /// <summary>Gets the event source, if any.</summary>
    public IQueryEventSource? Source => _source;

    /// <summary>Gets the current synchronization status.</summary>
    public QuerySyncStatus Status
    {
        get { lock (_gate) { return _status; } }
    }

    /// <summary>Raised when <see cref="Status"/> changes (on the thread that caused the change).</summary>
    public event Action<QuerySyncStatusChange>? StatusChanged;

    /// <summary>
    /// Gets the scope (tenant, workspace) whose events are applied; events of another scope are ignored, events without
    /// a scope always apply. <see langword="null"/> applies every scope.
    /// </summary>
    public string? Scope => Volatile.Read(ref _scope);

    /// <summary>
    /// Registers how events named <paramref name="eventName"/> change the cache. Several mappings may handle one event
    /// (each applied independently); they must agree on <typeparamref name="TEvent"/>. Dispose the result to remove the
    /// mapping - the source subscription for the event ends with its last mapping.
    /// </summary>
    /// <example>
    /// <code>
    /// var todos = QueryEntity.Create&lt;Todo, int&gt;(TodoKeys.All, t => t.Id, version: t => t.Version);
    /// sync.Map&lt;Todo&gt;("TodoChanged", m => m
    ///     .WithMetadata(t => new QueryEventMetadata { EntityId = t.Id.ToString(), Version = t.Version })
    ///     .Upsert(todos, t => t)
    ///     .Invalidate(TodoKeys.Stats));
    /// sync.Map&lt;int&gt;("TodoDeleted", m => m.Remove(todos, id => id).Invalidate(TodoKeys.Stats));
    /// </code>
    /// </example>
    public IDisposable Map<TEvent>(string eventName, Action<QueryEventMapping<TEvent>> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(configure);
        var mapping = new QueryEventMapping<TEvent>(eventName);
        configure(mapping);
        var covered = mapping.Seal();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _registrations.TryGetValue(eventName, out var existing);
            if (existing is not null and not EventRegistration<TEvent>)
            {
                throw new InvalidOperationException(
                    $"Event '{eventName}' is already mapped with payload type {existing.PayloadType.Name}, not {typeof(TEvent).Name}.");
            }

            var registration = (EventRegistration<TEvent>?)existing ?? new EventRegistration<TEvent>(eventName);
            registration.Add(mapping, covered);
            if (existing is null)
            {
                if (_source is { } source)
                {
                    registration.Subscription = source.Subscribe<TEvent>(eventName, payload => EnqueueFromSource(eventName, payload));
                }

                _registrations = new Dictionary<string, EventRegistration>(_registrations, StringComparer.Ordinal) { [eventName] = registration };
            }

            RecomputeCoverageLocked();
        }

        return new MappingHandle<TEvent>(this, eventName, mapping);
    }

    /// <summary>
    /// Starts the source (connecting it if needed) and completes once it is connected. The recovery that follows the
    /// connection runs in the background; observe <see cref="Status"/> for <see cref="QuerySyncStatus.Synchronized"/>.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _source?.StartAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// Queues an event and completes once it is processed, with its outcome. Waits for room when the queue is full.
    /// Cancelling the token cancels the wait only. Must not be called from a mapping handler (use <see cref="Post"/>).
    /// </summary>
    public async ValueTask<QueryEventOutcome> ProcessAsync<TEvent>(string eventName, TEvent payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (s_processing.Value == this)
        {
            throw new InvalidOperationException("ProcessAsync cannot be awaited from a mapping handler of the same synchronizer; use Post.");
        }

        OnReceived();
        var completion = new TaskCompletionSource<QueryEventOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new EventItem<TEvent>(eventName, payload, completion);
        if (!TryWrite(item))
        {
            try
            {
                await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _enqueued);
                DotNetQueryDiagnostics.SyncQueueDepth.Add(1, _nameTag);
            }
            catch (ChannelClosedException)
            {
                return QueryEventOutcome.Cancelled;
            }
        }

        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues an event without waiting. Returns <see langword="false"/> when the queue is full (the event is dropped and
    /// a recovery is scheduled, whatever the <see cref="QueryEventSyncOptions.OverflowPolicy"/>) or the synchronizer is
    /// disposed.
    /// </summary>
    public bool Post<TEvent>(string eventName, TEvent payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        OnReceived();
        if (TryWrite(new EventItem<TEvent>(eventName, payload, completion: null)))
        {
            return true;
        }

        OnDropped(eventName);
        return false;
    }

    /// <summary>
    /// Runs a recovery now (after the events already queued): the configured <see cref="QueryEventSyncOptions.Recovery"/>
    /// strategy, falling back to invalidating every covered query. Completes once the recovery ran and the refetches of
    /// observed queries it caused settled. Concurrent calls and automatic recoveries are coalesced.
    /// </summary>
    public async Task ResynchronizeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            (_manualWaiters ??= []).Add(completion);
        }

        RequestRecovery(QuerySyncRecoveryReason.Manual);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private List<TaskCompletionSource>? _manualWaiters;

    /// <summary>
    /// Records that this client caused a change correlated by <paramref name="correlationId"/> (for example the id it
    /// sent with a mutation), so mappings configured with <see cref="QueryEventMapping{TEvent}.IgnoreLocalEchoes"/> skip
    /// the server's echo of it. Remembered for the deduplication window.
    /// </summary>
    public void MarkLocalOrigin(string correlationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        lock (_localOrigins)
        {
            _localOrigins.Add(correlationId);
        }
    }

    /// <summary>
    /// Switches to another scope (for example after a tenant switch). Ordering state (entity versions and sequence
    /// checkpoints) is reset, since it belonged to the previous scope; the cache is not touched - reset or clear the
    /// previous scope's queries as the application requires.
    /// </summary>
    public void ChangeScope(string? scope)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _scope, scope);

        // On the loop, after the events already queued: one of the previous scope still being processed must not
        // re-seed the ordering state of the new one.
        Enqueue(new ActionItem(static owner =>
        {
            owner._versions.Clear();
            owner._sequences.Clear();
        }));
    }

    /// <summary>Gets the last applied sequence of <paramref name="stream"/> (<see langword="null"/>: the default stream), or <see langword="null"/> if none.</summary>
    public long? GetCheckpoint(string? stream = null) => _sequences.Get(stream ?? string.Empty);

    /// <summary>
    /// Sets the checkpoint of <paramref name="stream"/> - for example one persisted by the application before a restart,
    /// so a <see cref="QuerySyncRecovery.Replay"/> recovery resumes from it and older events are discarded as stale.
    /// </summary>
    public void SetCheckpoint(long sequence, string? stream = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SetCheckpointCore(stream, sequence);
    }

    internal void SetCheckpointCore(string? stream, long sequence) => _sequences.Set(stream ?? string.Empty, sequence);

    /// <summary>Returns a point-in-time view of the synchronizer, for diagnostics and DevTools.</summary>
    public QuerySyncStatistics GetStatistics()
    {
        Dictionary<string, int> mappings;
        QueryKey[] covered;
        QuerySyncStatus status;
        QuerySyncError[] errors;
        bool recoveryPending;
        lock (_gate)
        {
            mappings = new Dictionary<string, int>(_registrations.Count, StringComparer.Ordinal);
            foreach (var (name, registration) in _registrations)
            {
                mappings[name] = registration.MappingCount;
            }

            covered = _coveredKeys;
            status = _status;
            errors = [.. _errors];
            recoveryPending = _recoveryRequested != 0 || _recoveryRunning;
        }

        return new QuerySyncStatistics
        {
            Name = _options.Name,
            Status = status,
            Scope = Scope,
            Mappings = mappings,
            CoveredKeys = covered,
            Received = Interlocked.Read(ref _received),
            Applied = Interlocked.Read(ref _applied),
            Duplicates = Interlocked.Read(ref _duplicates),
            Stale = Interlocked.Read(ref _stale),
            Filtered = Interlocked.Read(ref _filtered),
            Rejected = Interlocked.Read(ref _rejected),
            Echoes = Interlocked.Read(ref _echoes),
            Gaps = Interlocked.Read(ref _gapCount),
            Failed = Interlocked.Read(ref _failed),
            Dropped = Interlocked.Read(ref _dropped),
            QueueDepth = _queue.Reader.CanCount ? _queue.Reader.Count : 0,
            Recoveries = Interlocked.Read(ref _recoveries),
            RecoveryFallbacks = Interlocked.Read(ref _recoveryFallbacks),
            RecoveryPending = recoveryPending,
            Checkpoints = _sequences.Snapshot(),
            LastEventAt = FromTicks(Interlocked.Read(ref _lastEventTicks)),
            LastSynchronizedAt = FromTicks(Interlocked.Read(ref _lastSynchronizedTicks)),
            RecentErrors = errors,
        };
    }

    /// <summary>
    /// Stops processing: detaches from the source (disposing it only if owned), cancels queued events (their
    /// <see cref="ProcessAsync"/> callers receive <see cref="QueryEventOutcome.Cancelled"/>) and a running recovery, and
    /// waits for the loop to exit. Refetches already started belong to the client and continue.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IQueryEventSource? source;
        List<EventRegistration> registrations;
        List<TaskCompletionSource>? waiters;
        lock (_gate)
        {
            source = _source;
            _source = null;
            registrations = [.. _registrations.Values];
            waiters = _manualWaiters;
            _manualWaiters = null;
        }

        if (source is not null && _sourceHandler is not null)
        {
            source.StateChanged -= _sourceHandler;
        }

        foreach (var registration in registrations)
        {
            registration.DisposeSubscription();
        }

        _queue.Writer.TryComplete();
        _stop.Cancel();
        waiters?.ForEach(static w => w.TrySetCanceled());

        // A handler disposing its own synchronizer runs on the loop: it must not wait for itself.
        if (s_processing.Value != this)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop reports its own failures; disposal must not throw.
            }
        }

        SetStatus(QuerySyncStatus.Disconnected, null);

        if (_ownsSource && source is not null)
        {
            try
            {
                if (source is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (source is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch (Exception ex)
            {
                RecordError(null, ex);
            }
        }
    }

    /// <summary>Starts disposal (see <see cref="DisposeAsync"/>) without waiting for the loop to exit.</summary>
    public void Dispose() => _ = DisposeAsync().AsTask();

    // ------------------------------------------------------------------ source and status

    private void OnSourceStateChanged(QueryEventSourceStateChange change)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        switch (change.State)
        {
            case QueryEventSourceState.Connecting:
                SetStatus(QuerySyncStatus.Connecting, change.Error);
                break;
            case QueryEventSourceState.Reconnecting:
                SetStatus(QuerySyncStatus.Reconnecting, change.Error);
                break;
            case QueryEventSourceState.Resubscribing:
                SetStatus(QuerySyncStatus.Resubscribing, change.Error);
                break;
            case QueryEventSourceState.Disconnected:
                if (change.Error is { } error)
                {
                    RecordError(null, error);
                }

                SetStatus(QuerySyncStatus.Disconnected, change.Error);
                break;
            case QueryEventSourceState.Connected:
                bool reconnect;
                lock (_gate)
                {
                    reconnect = _everConnected;
                    _everConnected = true;
                }

                if (reconnect)
                {
                    RequestRecovery(QuerySyncRecoveryReason.Reconnected);
                }
                else if (_options.RecoverOnConnect)
                {
                    RequestRecovery(QuerySyncRecoveryReason.Connected);
                }
                else
                {
                    SetStatus(QuerySyncStatus.Synchronized, null);
                }

                break;
        }
    }

    private bool IsConnectedLocked => _source is null || _source.State is QueryEventSourceState.Connected;

    private void SetStatus(QuerySyncStatus status, Exception? error)
    {
        QuerySyncStatus previous;
        lock (_gate)
        {
            previous = _status;
            if (previous == status)
            {
                return;
            }

            _status = status;
            if (status is QuerySyncStatus.Synchronized)
            {
                Interlocked.Exchange(ref _lastSynchronizedTicks, _time.GetUtcNow().UtcTicks);
            }
        }

        var change = new QuerySyncStatusChange(status, previous, error);
        if (StatusChanged is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<QuerySyncStatusChange>)handler)(change);
                }
                catch (Exception)
                {
                    // Subscribers must not break synchronization.
                }
            }
        }

        if (_diagnostics is { HasDiagnosticListeners: true } client)
        {
            client.RaiseDiagnostic(new QuerySyncStatusChangedEvent
            {
                Timestamp = _time.GetUtcNow(),
                Synchronizer = _options.Name,
                Status = status,
                Previous = previous,
                Error = error,
            });
        }
    }

    // ------------------------------------------------------------------ queueing

    private void OnReceived()
    {
        Interlocked.Increment(ref _received);
        Interlocked.Exchange(ref _lastEventTicks, _time.GetUtcNow().UtcTicks);
        DotNetQueryDiagnostics.SyncEventsReceived.Add(1, _nameTag);
    }

    private ValueTask EnqueueFromSource<TEvent>(string eventName, TEvent payload)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return default;
        }

        OnReceived();
        var item = new EventItem<TEvent>(eventName, payload, completion: null);
        if (TryWrite(item))
        {
            return default;
        }

        if (_options.OverflowPolicy is QueueOverflowPolicy.Wait)
        {
            return WriteWaitingAsync(item);
        }

        OnDropped(eventName);
        return default;
    }

    private async ValueTask WriteWaitingAsync(WorkItem item)
    {
        try
        {
            await _queue.Writer.WriteAsync(item, _stop.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _enqueued);
            DotNetQueryDiagnostics.SyncQueueDepth.Add(1, _nameTag);
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException)
        {
            // Disposed while waiting for room.
        }
    }

    private bool TryWrite(WorkItem item)
    {
        if (!_queue.Writer.TryWrite(item))
        {
            return false;
        }

        Interlocked.Increment(ref _enqueued);
        DotNetQueryDiagnostics.SyncQueueDepth.Add(1, _nameTag);
        return true;
    }

    private void Enqueue(WorkItem item)
    {
        if (!TryWrite(item))
        {
            _ = WriteWaitingAsync(item);
        }
    }

    private void OnDropped(string eventName)
    {
        Interlocked.Increment(ref _dropped);
        ReportOutcome(eventName, QueryEventOutcome.Dropped, default, replay: false, 0, null);
        RequestRecovery(QuerySyncRecoveryReason.QueueOverflow);
    }

    // ------------------------------------------------------------------ the processing loop

    private async Task RunLoopAsync()
    {
        s_processing.Value = this;
        var reader = _queue.Reader;
        var token = _stop.Token;
        try
        {
            while (true)
            {
                while (true)
                {
                    if (ShouldRecover(reader))
                    {
                        await RunRecoveryAsync(token).ConfigureAwait(false);
                        continue;
                    }

                    if (!reader.TryRead(out var item))
                    {
                        break;
                    }

                    Interlocked.Increment(ref _dequeued);
                    DotNetQueryDiagnostics.SyncQueueDepth.Add(-1, _nameTag);
                    await item.RunAsync(this, token).ConfigureAwait(false);
                    if (++_sinceFlush >= _options.MaxBatchSize)
                    {
                        FlushInvalidations();
                    }
                }

                // The burst is over: issue its coalesced invalidations.
                FlushInvalidations();
                if (Volatile.Read(ref _recoveryRequested) != 0)
                {
                    continue;
                }

                if (!await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Disposed.
        }
        catch (Exception ex)
        {
            RecordError(null, ex);
            SetStatus(QuerySyncStatus.RecoveryFailed, ex);
        }
        finally
        {
            while (reader.TryRead(out var item))
            {
                DotNetQueryDiagnostics.SyncQueueDepth.Add(-1, _nameTag);
                item.Cancel();
            }
        }
    }

    // A recovery runs once the events queued before it was requested are processed (they are older than the state it
    // reconciles to), or as soon as the queue is empty.
    private bool ShouldRecover(ChannelReader<WorkItem> reader) =>
        Volatile.Read(ref _recoveryRequested) != 0
        && (Interlocked.Read(ref _dequeued) >= Interlocked.Read(ref _recoveryWatermark) || reader.Count == 0);

    internal ValueTask<QueryEventOutcome> ProcessInlineAsync<TEvent>(string eventName, TEvent payload, bool replay, CancellationToken cancellationToken)
    {
        if (s_processing.Value != this)
        {
            throw new InvalidOperationException("Replayed events can only be applied while the recovery runs.");
        }

        OnReceived();
        return ProcessCoreAsync(eventName, payload, replay, cancellationToken);
    }

    private async ValueTask<QueryEventOutcome> ProcessCoreAsync<TEvent>(string eventName, TEvent payload, bool replay, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!Volatile.Read(ref _registrations).TryGetValue(eventName, out var registration))
        {
            return Report(QueryEventOutcome.NoMapping, default);
        }

        if (registration is not EventRegistration<TEvent> typed)
        {
            var mismatch = new InvalidOperationException(
                $"Event '{eventName}' carries {typeof(TEvent).Name}, but is mapped with {registration.PayloadType.Name}.");
            RecordError(eventName, mismatch);
            return Report(QueryEventOutcome.Rejected, default, mismatch);
        }

        QueryEventMetadata metadata;
        try
        {
            metadata = typed.ReadMetadata(payload);
        }
        catch (Exception ex)
        {
            RecordError(eventName, ex);
            return Report(QueryEventOutcome.Rejected, default, ex);
        }

        if (metadata.EventId is null && _options.RequireEventId)
        {
            return Report(QueryEventOutcome.Rejected, metadata);
        }

        if (Scope is { } scope && metadata.Scope is { } eventScope && !string.Equals(scope, eventScope, StringComparison.Ordinal))
        {
            return Report(QueryEventOutcome.OutOfScope, metadata);
        }

        if (metadata.EventId is { } id && _dedup.Contains(id))
        {
            return Report(QueryEventOutcome.Duplicate, metadata);
        }

        var stream = metadata.Stream ?? string.Empty;
        if (metadata.Sequence is { } sequence)
        {
            switch (_sequences.Check(stream, sequence, _options.ContiguousSequences))
            {
                case SequenceCheck.Stale:
                    return Report(QueryEventOutcome.Stale, metadata);
                case SequenceCheck.Gap:
                    // Held back: the recovery replays it (or reconciles past it).
                    _sequences.RecordGap(stream, sequence);
                    Interlocked.Increment(ref _gapCount);
                    RequestRecovery(QuerySyncRecoveryReason.SequenceGap);
                    return Report(QueryEventOutcome.Gap, metadata);
            }
        }

        if (metadata.EntityId is { } entity && metadata.Version is { } version && _versions.IsStale(entity, version))
        {
            // Handled (discarded): its stream position is consumed, or a contiguous stream would report a false gap.
            if (metadata.EventId is { } staleId)
            {
                _dedup.Add(staleId);
            }

            if (metadata.Sequence is { } staleSequence)
            {
                _sequences.Advance(stream, staleSequence);
            }

            return Report(QueryEventOutcome.Stale, metadata);
        }

        var changeSequence = Client.Cache.Runtime.NextSequence();
        bool applied = false, failed = false, echo = false;
        Exception? error = null;
        foreach (var mapping in typed.Mappings)
        {
            try
            {
                if (!mapping.Accepts(payload))
                {
                    continue;
                }

                if (mapping.IgnoresLocalEchoes && metadata.CorrelationId is { } correlation && IsLocalOrigin(correlation))
                {
                    echo = true;
                    continue;
                }

                await mapping.ApplyAsync(this, payload, metadata, changeSequence, replay, cancellationToken).ConfigureAwait(false);
                applied = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed = true;
                error ??= ex;
                RecordError(eventName, ex);
                try
                {
                    mapping.InvalidateTargets(this, payload);
                }
                catch (Exception)
                {
                    // Best effort: the failure is already recorded.
                }
            }
        }

        // The event is handled (applied, reconciled by invalidation, filtered or echoed): record its progress only now.
        if (metadata.EventId is { } processedId)
        {
            _dedup.Add(processedId);
        }

        if (metadata.Sequence is { } processedSequence)
        {
            _sequences.Advance(stream, processedSequence);
        }

        if (metadata.EntityId is { } entityId && metadata.Version is { } entityVersion)
        {
            _versions.Record(entityId, entityVersion);
        }

        var outcome = failed ? QueryEventOutcome.Failed
            : applied ? QueryEventOutcome.Applied
            : echo ? QueryEventOutcome.Echo
            : QueryEventOutcome.Filtered;
        if (applied || failed)
        {
            Volatile.Read(ref _relay)?.OnEventProcessed(eventName, metadata.EventId);
        }

        return Report(outcome, metadata, error);

        QueryEventOutcome Report(QueryEventOutcome result, QueryEventMetadata meta, Exception? failure = null)
        {
            ReportOutcome(eventName, result, meta, replay, started, failure);
            return result;
        }
    }

    private bool IsLocalOrigin(string correlationId)
    {
        lock (_localOrigins)
        {
            return _localOrigins.Contains(correlationId);
        }
    }

    private void ReportOutcome(string eventName, QueryEventOutcome outcome, QueryEventMetadata metadata, bool replay, long started, Exception? error)
    {
        switch (outcome)
        {
            case QueryEventOutcome.Applied: Interlocked.Increment(ref _applied); break;
            case QueryEventOutcome.Duplicate: Interlocked.Increment(ref _duplicates); break;
            case QueryEventOutcome.Stale: Interlocked.Increment(ref _stale); break;
            case QueryEventOutcome.Filtered or QueryEventOutcome.OutOfScope or QueryEventOutcome.NoMapping: Interlocked.Increment(ref _filtered); break;
            case QueryEventOutcome.Rejected: Interlocked.Increment(ref _rejected); break;
            case QueryEventOutcome.Echo: Interlocked.Increment(ref _echoes); break;
            case QueryEventOutcome.Failed: Interlocked.Increment(ref _failed); break;
        }

        if (DotNetQueryDiagnostics.SyncEventsProcessed.Enabled)
        {
            DotNetQueryDiagnostics.SyncEventsProcessed.Add(1, _nameTag, new("dotnetquery.sync.outcome", OutcomeNames[(int)outcome]));
        }

        if (_diagnostics is { HasDiagnosticListeners: true } client)
        {
            client.RaiseDiagnostic(new QuerySyncEventProcessedEvent
            {
                Timestamp = _time.GetUtcNow(),
                Synchronizer = _options.Name,
                EventName = eventName,
                Outcome = outcome,
                EventId = metadata.EventId,
                Sequence = metadata.Sequence,
                IsReplay = replay,
                Duration = started == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(started),
                Error = error,
            });
        }
    }

    private static readonly string[] OutcomeNames = Enum.GetNames<QueryEventOutcome>().Select(static n => n.ToLowerInvariant()).ToArray();

    // ------------------------------------------------------------------ cache operations

    /// <summary>Queues an invalidation; the burst's invalidations are issued together once the queue drains.</summary>
    internal void QueueInvalidation(QueryFilter filter)
    {
        lock (_pendingInvalidations)
        {
            _pendingInvalidations.Add(filter);
        }
    }

    /// <summary>Cancels and restarts a fetch that started before the event being processed.</summary>
    internal void Supersede(IQueryEntry entry, long changeSequence)
    {
        // Refetches belong to the client: they must not inherit the loop's execution context.
        using var noFlow = ExecutionContext.SuppressFlow();
        _ = entry.RefetchAsync(FetchMode.CancelAndRefetch, includeDisabled: false, CancellationToken.None, changeSequence);
    }

    private void FlushInvalidations()
    {
        _sinceFlush = 0;
        QueryFilter[] filters;
        lock (_pendingInvalidations)
        {
            if (_pendingInvalidations.Count == 0)
            {
                return;
            }

            filters = [.. _pendingInvalidations];
            _pendingInvalidations.Clear();
        }

        using var noFlow = ExecutionContext.SuppressFlow();
        foreach (var filter in Coalesce(filters))
        {
            try
            {
                _ = filter is { Key: { } key, Exact: false } && IsPlainPrefix(filter)
                    ? Client.InvalidateQueriesAsync(key)
                    : Client.InvalidateQueriesAsync(filter);
            }
            catch (Exception ex)
            {
                RecordError(null, ex);
            }
        }
    }

    private static bool IsPlainPrefix(QueryFilter filter) =>
        filter is { Key: not null, Exact: false, Type: QueryTypeFilter.All, Stale: null, Fetching: null, Status: null, Predicate: null };

    /// <summary>Drops filters subsumed by a plain prefix filter of the same batch (a burst refetches each query once).</summary>
    private static IEnumerable<QueryFilter> Coalesce(QueryFilter[] filters)
    {
        var prefixes = new List<QueryKey>();
        foreach (var filter in filters)
        {
            if (IsPlainPrefix(filter))
            {
                prefixes.Add(filter.Key!);
            }
        }

        foreach (var filter in filters)
        {
            if (filter.Key is { } key)
            {
                var plain = IsPlainPrefix(filter);
                var subsumed = false;
                foreach (var prefix in prefixes)
                {
                    // A strictly shorter prefix covers this one; an exact or narrower filter is covered by any prefix of it.
                    if (prefix.IsPrefixOf(key) && (!plain || prefix.Count < key.Count))
                    {
                        subsumed = true;
                        break;
                    }
                }

                if (subsumed)
                {
                    continue;
                }
            }

            yield return filter;
        }
    }

    // ------------------------------------------------------------------ recovery

    private void RequestRecovery(QuerySyncRecoveryReason reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        bool connected;
        lock (_gate)
        {
            if (_recoveryRequested == 0)
            {
                _recoveryReason = reason;
                Interlocked.Exchange(ref _recoveryWatermark, Interlocked.Read(ref _enqueued));
                Volatile.Write(ref _recoveryRequested, 1);
            }

            connected = IsConnectedLocked;
        }

        if (connected)
        {
            SetStatus(QuerySyncStatus.Synchronizing, null);
        }

        // Wake the loop if it is idle (a full queue means it is busy and will see the request).
        TryWrite(WakeItem.Instance);
    }

    private async Task RunRecoveryAsync(CancellationToken cancellationToken)
    {
        QuerySyncRecoveryReason reason;
        long generation;
        QueryKey[] covered;
        List<TaskCompletionSource>? waiters;
        bool connected;
        lock (_gate)
        {
            reason = _recoveryReason;
            Volatile.Write(ref _recoveryRequested, 0);
            _recoveryRunning = true;
            generation = ++_recoveryGeneration;
            covered = _coveredKeys;
            waiters = _manualWaiters;
            _manualWaiters = null;
            connected = IsConnectedLocked;
        }

        if (connected)
        {
            SetStatus(QuerySyncStatus.Synchronizing, null);
        }

        var started = Stopwatch.GetTimestamp();
        var activity = DotNetQueryDiagnostics.ActivitySource.StartActivity("dotnetquery.sync.recovery", ActivityKind.Internal);
        activity?.SetTag("dotnetquery.sync.name", _options.Name);
        activity?.SetTag("dotnetquery.sync.reason", reason.ToString());

        // Invalidations of events processed so far go out first; the recovery reconciles on top of them.
        FlushInvalidations();

        var strategy = _options.Recovery;
        var recovered = false;
        Exception? error = null;
        if (strategy.Recover is { } recover)
        {
            var context = new QuerySyncRecoveryContext(this, reason, covered);
            try
            {
                recovered = await recover(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity?.Dispose();
                waiters?.ForEach(static w => w.TrySetCanceled());
                throw;
            }
            catch (Exception ex)
            {
                error = ex;
                RecordError(null, ex);
            }
            finally
            {
                context.Complete();
            }

            if (recovered && _options.ContiguousSequences && _sequences.HasGap)
            {
                recovered = false; // the replay did not close the gap
            }

            if (recovered && strategy.Kind is not QuerySyncRecoveryKind.Replay && !context.CheckpointSet)
            {
                _sequences.ResetBaseline();
            }

            FlushInvalidations();
        }

        var fellBack = strategy.Recover is not null && !recovered;
        var refetches = Task.CompletedTask;
        if (!recovered)
        {
            try
            {
                _sequences.ResetBaseline();
                using var noFlow = ExecutionContext.SuppressFlow();
                var tasks = new Task[covered.Length];
                for (var i = 0; i < covered.Length; i++)
                {
                    tasks[i] = Client.InvalidateQueriesAsync(covered[i], CancellationToken.None);
                }

                refetches = Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                // Practically unreachable (invalidation never throws for fetch failures), but never leave it silent.
                error ??= ex;
                RecordError(null, ex);
                FinishRecovery(generation, reason, strategy.Kind, fellBack, succeeded: false, error, started, activity, waiters);
                return;
            }
        }

        Interlocked.Increment(ref _recoveries);
        if (fellBack)
        {
            Interlocked.Increment(ref _recoveryFallbacks);
        }

        // Relayed participants saw every replayed event; after any other recovery they reconcile everything covered.
        if (!(recovered && strategy.Kind is QuerySyncRecoveryKind.Replay))
        {
            Volatile.Read(ref _relay)?.OnResynchronized();
        }

        _ = CompleteRecoveryAsync(refetches, generation, reason, strategy.Kind, fellBack, error, started, activity, waiters);
    }

    private async Task CompleteRecoveryAsync(
        Task refetches,
        long generation,
        QuerySyncRecoveryReason reason,
        QuerySyncRecoveryKind kind,
        bool fellBack,
        Exception? error,
        long started,
        Activity? activity,
        List<TaskCompletionSource>? waiters)
    {
        try
        {
            await refetches.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Refetch failures are captured in each query's state.
        }

        FinishRecovery(generation, reason, kind, fellBack, succeeded: true, error, started, activity, waiters);
    }

    private void FinishRecovery(
        long generation,
        QuerySyncRecoveryReason reason,
        QuerySyncRecoveryKind kind,
        bool fellBack,
        bool succeeded,
        Exception? error,
        long started,
        Activity? activity,
        List<TaskCompletionSource>? waiters)
    {
        var duration = Stopwatch.GetElapsedTime(started);
        activity?.SetTag("dotnetquery.sync.fell_back", fellBack);
        activity?.SetStatus(succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error, error?.Message);
        activity?.Dispose();
        DotNetQueryDiagnostics.SyncRecoveryDuration.Record(duration.TotalMilliseconds, _nameTag);
        DotNetQueryDiagnostics.SyncRecoveries.Add(
            1,
            _nameTag,
            new("dotnetquery.sync.reason", reason.ToString()),
            new("dotnetquery.sync.result", !succeeded ? "failed" : fellBack ? "fallback" : "recovered"));

        if (_diagnostics is { HasDiagnosticListeners: true } client)
        {
            client.RaiseDiagnostic(new QuerySyncRecoveryCompletedEvent
            {
                Timestamp = _time.GetUtcNow(),
                Synchronizer = _options.Name,
                Reason = reason,
                Strategy = kind,
                FellBack = fellBack,
                Succeeded = succeeded,
                Duration = duration,
                Error = error,
            });
        }

        bool current;
        bool connected;
        lock (_gate)
        {
            current = generation == _recoveryGeneration && _recoveryRequested == 0;
            if (generation == _recoveryGeneration)
            {
                _recoveryRunning = false;
            }

            connected = IsConnectedLocked;
        }

        waiters?.ForEach(static w => w.TrySetResult());

        // A newer recovery (requested meanwhile) owns the status; so does a connection lost meanwhile.
        if (current && connected && Volatile.Read(ref _disposed) == 0)
        {
            SetStatus(succeeded ? QuerySyncStatus.Synchronized : QuerySyncStatus.RecoveryFailed, succeeded ? null : error);
        }
    }

    // ------------------------------------------------------------------ relaying

    private QueryEventRelay? _relay;

    internal void AttachRelay(QueryEventRelay relay)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _relay, relay, null) is not null)
        {
            throw new InvalidOperationException("The synchronizer already has a relay.");
        }
    }

    internal void DetachRelay(QueryEventRelay relay) => Interlocked.CompareExchange(ref _relay, null, relay);

    /// <summary>
    /// Applies a notice relayed by another participant: invalidates what the mappings of <paramref name="eventName"/>
    /// cover (everything covered when <see langword="null"/>), on the loop and coalesced with its other invalidations,
    /// unless this synchronizer already processed the same event itself.
    /// </summary>
    internal void ApplyRelayed(string? eventName, string? eventId)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Enqueue(new ActionItem(owner =>
        {
            if (eventId is not null && owner._dedup.Contains(eventId))
            {
                return;
            }

            IReadOnlyList<QueryKey> keys;
            if (eventName is null)
            {
                keys = Volatile.Read(ref owner._coveredKeys);
            }
            else if (Volatile.Read(ref owner._registrations).TryGetValue(eventName, out var registration))
            {
                keys = registration.CoveredKeys;
            }
            else
            {
                return; // not an event this participant maps
            }

            foreach (var key in keys)
            {
                owner.QueueInvalidation(QueryFilter.ForKey(key));
            }
        }));
    }

    // ------------------------------------------------------------------ registrations

    private void RemoveMapping<TEvent>(string eventName, QueryEventMapping<TEvent> mapping)
    {
        EventRegistration? emptied = null;
        lock (_gate)
        {
            if (!_registrations.TryGetValue(eventName, out var registration) || registration is not EventRegistration<TEvent> typed || !typed.Remove(mapping))
            {
                return;
            }

            if (typed.MappingCount == 0)
            {
                var copy = new Dictionary<string, EventRegistration>(_registrations, StringComparer.Ordinal);
                copy.Remove(eventName);
                _registrations = copy;
                emptied = typed;
            }

            RecomputeCoverageLocked();
        }

        emptied?.DisposeSubscription();
    }

    private void RecomputeCoverageLocked()
    {
        var keys = new List<QueryKey>();
        foreach (var registration in _registrations.Values)
        {
            keys.AddRange(registration.CoveredKeys);
        }

        // Keep only the broadest prefixes, once each.
        var normalized = new List<QueryKey>();
        foreach (var key in keys.Distinct())
        {
            var subsumed = false;
            foreach (var other in keys)
            {
                if (other != key && other.IsPrefixOf(key))
                {
                    subsumed = true;
                    break;
                }
            }

            if (!subsumed)
            {
                normalized.Add(key);
            }
        }

        _coveredKeys = [.. normalized];
    }

    private void RecordError(string? eventName, Exception error)
    {
        var record = new QuerySyncError(_time.GetUtcNow(), eventName, error.Message, error.GetType().Name);
        lock (_gate)
        {
            _errors.Enqueue(record);
            while (_errors.Count > MaxRecentErrors)
            {
                _errors.Dequeue();
            }
        }
    }

    private static DateTimeOffset? FromTicks(long ticks) => ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);

    private abstract class WorkItem
    {
        public abstract ValueTask RunAsync(QueryEventSynchronizer owner, CancellationToken cancellationToken);

        public virtual void Cancel()
        {
        }
    }

    private sealed class EventItem<TEvent>(string eventName, TEvent payload, TaskCompletionSource<QueryEventOutcome>? completion) : WorkItem
    {
        public override async ValueTask RunAsync(QueryEventSynchronizer owner, CancellationToken cancellationToken)
        {
            QueryEventOutcome outcome;
            try
            {
                outcome = await owner.ProcessCoreAsync(eventName, payload, replay: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion?.TrySetResult(QueryEventOutcome.Cancelled);
                throw;
            }

            if (completion is not null)
            {
                // Someone awaits this event: when it ends a burst, issue the burst's invalidations before reporting it
                // processed (inside a burst they are still coalesced).
                if (owner._queue.Reader.Count == 0)
                {
                    owner.FlushInvalidations();
                }

                completion.TrySetResult(outcome);
            }
        }

        public override void Cancel() => completion?.TrySetResult(QueryEventOutcome.Cancelled);
    }

    private sealed class WakeItem : WorkItem
    {
        public static WakeItem Instance { get; } = new();

        public override ValueTask RunAsync(QueryEventSynchronizer owner, CancellationToken cancellationToken) => default;
    }

    private sealed class ActionItem(Action<QueryEventSynchronizer> action) : WorkItem
    {
        public override ValueTask RunAsync(QueryEventSynchronizer owner, CancellationToken cancellationToken)
        {
            action(owner);
            return default;
        }
    }

    private sealed class MappingHandle<TEvent>(QueryEventSynchronizer owner, string eventName, QueryEventMapping<TEvent> mapping) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.RemoveMapping(eventName, mapping);
            }
        }
    }

    private abstract class EventRegistration(string name)
    {
        public string Name { get; } = name;

        public abstract Type PayloadType { get; }

        public abstract int MappingCount { get; }

        public abstract IReadOnlyList<QueryKey> CoveredKeys { get; }

        public IDisposable? Subscription { get; set; }

        public void DisposeSubscription()
        {
            var subscription = Subscription;
            Subscription = null;
            try
            {
                subscription?.Dispose();
            }
            catch (Exception)
            {
                // A transport failing to unsubscribe must not break disposal.
            }
        }
    }

    // Mutated under the owner's gate; read lock-free by the loop (copy-on-write arrays).
    private sealed class EventRegistration<TEvent>(string name) : EventRegistration(name)
    {
        private (QueryEventMapping<TEvent> Mapping, IReadOnlyList<QueryKey> Covered)[] _entries = [];
        private QueryEventMapping<TEvent>[] _mappings = [];
        private Func<TEvent, QueryEventMetadata>? _metadata;

        public override Type PayloadType => typeof(TEvent);

        public override int MappingCount => Volatile.Read(ref _mappings).Length;

        public QueryEventMapping<TEvent>[] Mappings => Volatile.Read(ref _mappings);

        public override IReadOnlyList<QueryKey> CoveredKeys => _entries.SelectMany(static e => e.Covered).ToArray();

        public void Add(QueryEventMapping<TEvent> mapping, IReadOnlyList<QueryKey> covered)
        {
            if (mapping.Metadata is not null && _metadata is not null)
            {
                throw new InvalidOperationException(
                    $"The metadata of event '{Name}' is already configured by another mapping; declare WithMetadata on one mapping only.");
            }

            _entries = [.. _entries, (mapping, covered)];
            Publish();
        }

        public bool Remove(QueryEventMapping<TEvent> mapping)
        {
            var index = Array.FindIndex(_entries, e => ReferenceEquals(e.Mapping, mapping));
            if (index < 0)
            {
                return false;
            }

            _entries = [.. _entries[..index], .. _entries[(index + 1)..]];
            Publish();
            return true;
        }

        public QueryEventMetadata ReadMetadata(TEvent payload) =>
            Volatile.Read(ref _metadata) is { } read ? read(payload)
            : payload is IQueryEventMetadataProvider provider ? provider.GetQueryEventMetadata()
            : default;

        private void Publish()
        {
            Volatile.Write(ref _metadata, _entries.Select(static e => e.Mapping.Metadata).FirstOrDefault(static m => m is not null));
            Volatile.Write(ref _mappings, _entries.Select(static e => e.Mapping).ToArray());
        }
    }
}
