using System.Collections.Concurrent;

namespace DotNetQuery;

/// <summary>
/// Queues mutation invocations while offline and replays them, in order, when connectivity is restored.
/// </summary>
/// <remarks>
/// Enqueue a mutation as a closure over its variables. While online, the closure runs immediately;
/// while offline, it is queued and flushed (oldest first) on the next reconnect. Because each entry is
/// a delegate, the queue is fully type-safe with no boxing of variables or reflection.
/// </remarks>
public sealed class OfflineMutationQueue : IDisposable
{
    private readonly IConnectivityProvider _connectivity;
    private readonly ConcurrentQueue<Func<CancellationToken, Task>> _pending = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Action<bool> _handler;

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

    /// <summary>Gets the number of mutations currently waiting to be replayed.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Runs <paramref name="mutation"/> immediately if online; otherwise queues it for replay on reconnect.
    /// </summary>
    public Task EnqueueAsync(Func<CancellationToken, Task> mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (_connectivity.IsOnline)
        {
            return mutation(cancellationToken);
        }

        _pending.Enqueue(mutation);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Replays queued mutations in order (oldest first) while connectivity holds. If a mutation throws,
    /// it propagates and the remaining mutations stay queued for the next flush.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_connectivity.IsOnline && _pending.TryDequeue(out var mutation))
            {
                await mutation(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>Unsubscribes from connectivity changes and releases resources.</summary>
    public void Dispose()
    {
        _connectivity.ConnectivityChanged -= _handler;
        _flushGate.Dispose();
    }
}
