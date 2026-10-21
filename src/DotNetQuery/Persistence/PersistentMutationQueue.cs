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

/// <summary>Options for a <see cref="PersistentMutationQueue"/>.</summary>
public sealed record PersistentMutationQueueOptions
{
    /// <summary>Serialization options for mutation variables (reflection-based handlers). Defaults to <see cref="JsonSerializerDefaults.Web"/>.</summary>
    public JsonSerializerOptions? JsonOptions { get; init; }

    /// <summary>The clock used to timestamp queued mutations. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
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
/// SQLite database) so <c>ClearAsync</c> on the query cache store cannot drop queued writes.
/// </para>
/// </remarks>
public sealed class PersistentMutationQueue : IDisposable
{
    private const string KeyPrefix = "dnq-mutation:";
    private static readonly JsonSerializerOptions DefaultJson = new(JsonSerializerDefaults.Web);

    private readonly IQueryPersister _store;
    private readonly IConnectivityProvider _connectivity;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Handler> _handlers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly List<Entry> _pending = [];
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly SemaphoreSlim _enqueueGate = new(1, 1);
    private readonly Action<bool> _connectivityHandler;
    private long _sequence;
    private int _disposed;

    /// <summary>Creates a queue over a store and a connectivity provider.</summary>
    public PersistentMutationQueue(IQueryPersister store, IConnectivityProvider connectivity, PersistentMutationQueueOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
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

    /// <summary>
    /// Registers the handler that runs mutations queued under <paramref name="mutationKey"/>, with variables
    /// serialized by reflection-based JSON. In trimmed or NativeAOT apps use the <see cref="JsonTypeInfo{T}"/> overload.
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization of TVariables may require types that cannot be statically analyzed. Use the JsonTypeInfo<TVariables> overload.")]
    [RequiresDynamicCode("JSON serialization of TVariables may require runtime code generation. Use the JsonTypeInfo<TVariables> overload.")]
    public PersistentMutationQueue RegisterHandler<TVariables>(string mutationKey, Func<TVariables, CancellationToken, Task> handler)
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
        ArgumentNullException.ThrowIfNull(typeInfo);
        return Add(
            mutationKey,
            handler,
            v => JsonSerializer.Serialize(v, typeInfo),
            json => JsonSerializer.Deserialize(json, typeInfo)!);
    }

    private PersistentMutationQueue Add<TVariables>(
        string mutationKey,
        Func<TVariables, CancellationToken, Task> handler,
        Func<TVariables, string> serialize,
        Func<string, TVariables> deserialize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationKey);
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[mutationKey] = new Handler(
            typeof(TVariables),
            variables => serialize((TVariables)variables!),
            (json, ct) => handler(deserialize(json), ct));
        return this;
    }

    /// <summary>
    /// Durably queues a mutation, then (when online) replays the queue in order. Completes once the mutation
    /// is stored and, if online, the flush has run. Failures are reported via <see cref="MutationFailed"/>.
    /// </summary>
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
            var id = $"{enqueuedAt.UtcTicks:D19}-{Interlocked.Increment(ref _sequence):D10}";
            await _store.SaveAsync(new PersistedQuery(KeyPrefix + id, payload, enqueuedAt), cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _pending.Add(new Entry(new PendingMutation(id, mutationKey, enqueuedAt), payload));
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
    /// <returns>The number of mutations restored.</returns>
    public async Task<int> RestoreAsync(CancellationToken cancellationToken = default)
    {
        int restored;

        // Exclude concurrent enqueues and flushes while merging, so an entry being written or replayed right
        // now can neither be loaded twice nor resurrected after it completed.
        await _enqueueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var stored = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
                restored = Merge(stored);
            }
            finally
            {
                _flushGate.Release();
            }
        }
        finally
        {
            _enqueueGate.Release();
        }

        if (restored > 0)
        {
            RaisePendingChanged();
        }

        if (_connectivity.IsOnline)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return restored;
    }

    private int Merge(IReadOnlyList<PersistedQuery> stored)
    {
        var restored = 0;
        lock (_gate)
        {
            var known = _pending.Select(e => e.Info.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var item in stored.Where(s => s.Key.StartsWith(KeyPrefix, StringComparison.Ordinal)).OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                var id = item.Key[KeyPrefix.Length..];
                if (known.Contains(id) || !TryReadEnvelope(item.Payload, out var mutationKey, out _))
                {
                    continue;
                }

                _pending.Add(new Entry(new PendingMutation(id, mutationKey, item.UpdatedAt), item.Payload));
                restored++;
            }

            _pending.Sort(static (a, b) => string.CompareOrdinal(a.Info.Id, b.Info.Id));
        }

        return restored;
    }

    /// <summary>
    /// Replays queued mutations in order while connectivity holds. Never throws for mutation failures;
    /// only cancellation propagates, leaving the remaining mutations queued.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_connectivity.IsOnline)
            {
                Entry? head;
                lock (_gate)
                {
                    head = _pending.Count > 0 ? _pending[0] : null;
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

                try
                {
                    await handler.Run(variablesJson, cancellationToken).ConfigureAwait(false);
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

                    await RemoveAsync(head).ConfigureAwait(false);
                    RaiseFailed(head.Info, ex);
                    continue;
                }

                await RemoveAsync(head).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushGate.Release();
        }
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

    private static string WriteEnvelope(string mutationKey, string variablesJson)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mutationKey", mutationKey);
            writer.WriteString("variables", variablesJson);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
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

    private sealed record Handler(Type VariablesType, Func<object?, string> Serialize, Func<string, CancellationToken, Task> Run);

    private sealed record Entry(PendingMutation Info, string Payload);
}
