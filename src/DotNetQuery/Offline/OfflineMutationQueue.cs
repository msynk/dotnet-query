namespace DotNetQuery;

/// <summary>
/// Queues mutation invocations while offline and replays them, in order, when connectivity is restored.
/// </summary>
/// <remarks>
/// <para>
/// Enqueue a mutation as a closure over its variables. While online with nothing queued, the closure runs
/// immediately; otherwise it is queued behind earlier work and flushed (oldest first) on the next
/// reconnect, so writes always reach the server in the order they were made. Because each entry is a
/// delegate, the queue is fully type-safe with no boxing of variables or reflection.
/// </para>
/// <para>
/// During a flush, a mutation that fails because connectivity dropped again stays at the head of the queue
/// for the next reconnect. A mutation that fails while still online is dropped (so one bad request cannot
/// block the queue forever) and reported through <see cref="MutationFailed"/>.
/// </para>
/// <para>
/// The queue lives in memory. To survive app restarts, use <c>PersistentMutationQueue</c> from the
/// <c>DotNetQuery.Persistence</c> package, which stores serializable mutation descriptors instead.
/// </para>
/// </remarks>
public sealed class OfflineMutationQueue : IDisposable
{
    private readonly IConnectivityProvider _connectivity;
    private readonly object _gate = new();
    private readonly LinkedList<Func<CancellationToken, Task>> _pending = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Action<bool> _handler;
    private bool _flushing;
    private int _disposed;

    /// <summary>Creates a queue bound to a connectivity provider.</summary>
    public OfflineMutationQueue(IConnectivityProvider connectivity)
    {
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
        _handler = online =>
        {
            if (online)
            {
                _ = FlushAsync();
            }
        };
        _connectivity.ConnectivityChanged += _handler;
    }

    /// <summary>Raised when a replayed mutation fails while online and is dropped from the queue.</summary>
    public event Action<Exception>? MutationFailed;

    /// <summary>Raised whenever <see cref="PendingCount"/> changes (useful for an "N changes waiting to sync" badge).</summary>
    public event Action? PendingChanged;

    /// <summary>Gets the number of mutations currently waiting to be replayed.</summary>
    public int PendingCount
    {
        get { lock (_gate) { return _pending.Count; } }
    }

    /// <summary>
    /// Runs <paramref name="mutation"/> immediately if online and nothing is queued ahead of it; otherwise
    /// queues it for replay. The returned task tracks the immediate run, or completes once the mutation is
    /// queued.
    /// </summary>
    public Task EnqueueAsync(Func<CancellationToken, Task> mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        bool runNow;
        lock (_gate)
        {
            runNow = _connectivity.IsOnline && _pending.Count == 0 && !_flushing;
            if (!runNow)
            {
                _pending.AddLast(mutation);
            }
        }

        if (runNow)
        {
            return mutation(cancellationToken);
        }

        RaisePendingChanged();
        if (_connectivity.IsOnline)
        {
            _ = FlushAsync(CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Replays queued mutations in order (oldest first) while connectivity holds. Never throws for mutation
    /// failures (see <see cref="MutationFailed"/>); only cancellation of <paramref name="cancellationToken"/>
    /// propagates, leaving the remaining mutations queued.
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
            lock (_gate)
            {
                _flushing = true;
            }

            while (true)
            {
                LinkedListNode<Func<CancellationToken, Task>>? head;
                lock (_gate)
                {
                    head = _connectivity.IsOnline ? _pending.First : null;
                }

                if (head is null)
                {
                    break;
                }

                try
                {
                    await head.Value(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!_connectivity.IsOnline)
                    {
                        break; // dropped offline mid-flush: keep it at the head for the next reconnect
                    }

                    Dequeue(head);
                    RaiseFailed(ex);
                    continue;
                }

                Dequeue(head);
            }
        }
        finally
        {
            lock (_gate)
            {
                _flushing = false;
            }

            _flushGate.Release();
        }
    }

    private void Dequeue(LinkedListNode<Func<CancellationToken, Task>> node)
    {
        lock (_gate)
        {
            if (node.List is not null)
            {
                _pending.Remove(node);
            }
        }

        RaisePendingChanged();
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

    private void RaiseFailed(Exception error)
    {
        try
        {
            MutationFailed?.Invoke(error);
        }
        catch (Exception)
        {
            // Subscriber errors must not break the queue.
        }
    }

    /// <summary>Unsubscribes from connectivity changes and releases resources. Queued mutations are discarded.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connectivity.ConnectivityChanged -= _handler;
    }
}
