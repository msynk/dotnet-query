using System.Runtime.InteropServices;

namespace DotNetQuery;

/// <summary>
/// Wraps every fetch attempt of a client - for logging, metrics, tracing, adding ambient context, or translating
/// errors. Interceptors registered in <see cref="QueryClientOptions.Interceptors"/> run in registration order (the
/// first is outermost) around each attempt, including retries; each calls the next step to continue.
/// </summary>
/// <example>
/// <code>
/// public sealed class LoggingInterceptor(ILogger logger) : IQueryInterceptor
/// {
///     public async Task&lt;T&gt; InterceptAsync&lt;T&gt;(QueryInterceptorContext context, Func&lt;CancellationToken, Task&lt;T&gt;&gt; next, CancellationToken cancellationToken)
///     {
///         logger.LogDebug("Fetching {Key} (attempt {Attempt})", context.Key, context.Attempt);
///         return await next(cancellationToken);
///     }
/// }
/// </code>
/// </example>
public interface IQueryInterceptor
{
    /// <summary>Runs one fetch attempt through this interceptor.</summary>
    /// <typeparam name="T">The query's data type.</typeparam>
    /// <param name="context">Which query and attempt is running.</param>
    /// <param name="next">The rest of the pipeline (ending in the query's fetcher).</param>
    /// <param name="cancellationToken">Cancels the attempt (supersession, cancellation, timeout).</param>
    Task<T> InterceptAsync<T>(QueryInterceptorContext context, Func<CancellationToken, Task<T>> next, CancellationToken cancellationToken);
}

/// <summary>Describes the fetch attempt an <see cref="IQueryInterceptor"/> wraps.</summary>
/// <param name="Key">The key of the query being fetched.</param>
/// <param name="Attempt">The 1-based attempt number (2 and above are retries).</param>
public sealed record QueryInterceptorContext(QueryKey Key, int Attempt);

/// <summary>
/// Gates every fetch attempt of a client (see <see cref="QueryClientOptions.FetchRateLimiter"/>) - for example to stay
/// within an API's request quota. Adapting a <c>System.Threading.RateLimiting.RateLimiter</c> takes a few lines:
/// </summary>
/// <example>
/// <code>
/// public sealed class RateLimiterAdapter(RateLimiter limiter) : IFetchRateLimiter
/// {
///     public async ValueTask&lt;IDisposable?&gt; AcquireAsync(QueryKey key, CancellationToken cancellationToken)
///     {
///         var lease = await limiter.AcquireAsync(1, cancellationToken);
///         if (lease.IsAcquired) return lease;
///         var error = new QueryRateLimitedException($"Rate limit exceeded for {key}.");
///         if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)) error.Data[HttpRetryPolicy.RetryAfterDataKey] = retryAfter;
///         lease.Dispose();
///         throw error;
///     }
/// }
/// </code>
/// </example>
public interface IFetchRateLimiter
{
    /// <summary>
    /// Waits until a fetch attempt for <paramref name="key"/> may run and returns a permit to dispose when it ends (or
    /// <see langword="null"/>). Throw (typically <see cref="QueryRateLimitedException"/>) to reject the attempt; the
    /// error is handled like a fetch failure, so the query's retry policy applies.
    /// </summary>
    ValueTask<IDisposable?> AcquireAsync(QueryKey key, CancellationToken cancellationToken);
}

/// <summary>
/// The error an <see cref="IFetchRateLimiter"/> rejects a fetch attempt with. Put a retry-after hint in
/// <see cref="Exception.Data"/> under <see cref="HttpRetryPolicy.RetryAfterDataKey"/> for
/// <see cref="HttpRetryPolicy.RespectRetryAfter"/>.
/// </summary>
public sealed class QueryRateLimitedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public QueryRateLimitedException()
        : this("The fetch was rejected by the client's rate limiter.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public QueryRateLimitedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public QueryRateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Limits how many fetch attempts of a client run at once, admitting waiting attempts by priority (higher first),
/// then in arrival order. Waiting honors cancellation.
/// </summary>
internal sealed class FetchLimiter(int maxConcurrent)
{
    private readonly object _gate = new();
    private readonly PriorityQueue<Waiter, (int Priority, long Sequence)> _waiters = new();
    private int _active;
    private long _sequence;

    public int Active
    {
        get { lock (_gate) { return _active; } }
    }

    public ValueTask<IDisposable> AcquireAsync(int priority, CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_gate)
        {
            if (_active < maxConcurrent)
            {
                _active++;
                return new ValueTask<IDisposable>(new Slot(this));
            }

            waiter = new Waiter(new Slot(this));
            _waiters.Enqueue(waiter, (-priority, _sequence++));
        }

        return new ValueTask<IDisposable>(waiter.WaitAsync(cancellationToken));
    }

    private void Release()
    {
        lock (_gate)
        {
            // Hand the slot to the best waiter that is still waiting; cancelled waiters are skipped.
            while (_waiters.TryDequeue(out var waiter, out _))
            {
                if (waiter.TryGrant())
                {
                    return;
                }
            }

            _active--;
        }
    }

    private sealed class Slot(FetchLimiter owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }

    private sealed class Waiter(Slot slot)
    {
        private readonly TaskCompletionSource<IDisposable> _granted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryGrant() => _granted.TrySetResult(slot);

        public async Task<IDisposable> WaitAsync(CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(static state => ((TaskCompletionSource<IDisposable>)state!).TrySetCanceled(), _granted))
            {
                return await _granted.Task.ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// Defers query notifications raised on the current asynchronous flow until the outermost batch ends, so a burst of
/// cache writes reaches observers (and UIs) as one wave. See <see cref="QueryNotifications.Batch"/>.
/// </summary>
internal sealed class NotificationBatchScope : IDisposable
{
    private static readonly AsyncLocal<NotificationBatchScope?> s_current = new();

    private readonly object _gate = new();
    private readonly List<(object Owner, Action Drain)> _pending = [];
    private readonly HashSet<object> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly NotificationBatchScope? _parent;
    private bool _completed;

    private NotificationBatchScope(NotificationBatchScope? parent) => _parent = parent;

    public static NotificationBatchScope Begin()
    {
        var scope = new NotificationBatchScope(s_current.Value);
        s_current.Value = scope;
        return scope;
    }

    /// <summary>Queues <paramref name="drain"/> for the end of the active batch; returns <see langword="false"/> when there is none.</summary>
    public static bool TryDefer(object owner, Action drain)
    {
        for (var scope = s_current.Value; scope is not null; scope = scope._parent)
        {
            if (scope.TryAdd(owner, drain))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryAdd(object owner, Action drain)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }

            if (_owners.Add(owner))
            {
                _pending.Add((owner, drain));
            }

            return true;
        }
    }

    public void Dispose()
    {
        (object Owner, Action Drain)[] pending;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            pending = [.. _pending];
        }

        if (ReferenceEquals(s_current.Value, this))
        {
            s_current.Value = _parent;
        }

        // Nested: hand the work to the enclosing batch; outermost (or enclosing already ended): deliver now.
        foreach (var (owner, drain) in pending)
        {
            if (_parent is null || !_parent.TryAdd(owner, drain))
            {
                drain();
            }
        }
    }
}

/// <summary>Batches query notifications (see <see cref="Batch"/>).</summary>
public static class QueryNotifications
{
    /// <summary>
    /// Starts a notification batch on the current asynchronous flow: observers of queries written inside it are
    /// notified once the returned scope is disposed (one notification wave instead of one per write), in order and
    /// without losing transitions. Batches nest; only the outermost delivers. Writes from other flows are not delayed.
    /// </summary>
    /// <example>
    /// <code>
    /// using (QueryNotifications.Batch())
    /// {
    ///     foreach (var todo in imported) { client.SetQueryData(TodoKeys.ById(todo.Id), todo); }
    /// }   // observers are notified here
    /// </code>
    /// </example>
    public static IDisposable Batch() => NotificationBatchScope.Begin();
}

/// <summary>
/// Invokes a callback after each gen-2 garbage collection, for as long as the target is alive (the pattern the runtime's
/// array pools use to trim under memory pressure). The callback runs on the finalizer thread and must be fast.
/// </summary>
internal sealed class Gen2GcCallback
{
    private readonly Func<object, bool> _callback;
    private GCHandle _target;

    private Gen2GcCallback(Func<object, bool> callback, object target)
    {
        _callback = callback;
        _target = GCHandle.Alloc(target, GCHandleType.Weak);
    }

    /// <summary>Registers <paramref name="callback"/>; it stops when it returns <see langword="false"/> or the target is collected.</summary>
    public static void Register(Func<object, bool> callback, object target) => _ = new Gen2GcCallback(callback, target);

    ~Gen2GcCallback()
    {
        if (_target.Target is not { } target)
        {
            _target.Free();
            return;
        }

        try
        {
            if (!_callback(target))
            {
                _target.Free();
                return;
            }
        }
        catch (Exception)
        {
            // Never let a callback take down the finalizer thread.
        }

        GC.ReRegisterForFinalize(this);
    }
}
