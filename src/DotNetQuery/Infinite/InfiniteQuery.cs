namespace DotNetQuery;

/// <summary>
/// An observable infinite (paginated) query. Supports forward and backward pagination via
/// <see cref="FetchNextPageAsync"/> and <see cref="FetchPreviousPageAsync"/>, exposing
/// <see cref="InfiniteQueryState{TParam, TPage}.HasNextPage"/> and
/// <see cref="InfiniteQueryState{TParam, TPage}.HasPreviousPage"/>.
/// </summary>
/// <remarks>
/// The accumulated pages are stored in the query cache under <see cref="Key"/> as an
/// <see cref="InfiniteData{TParam, TPage}"/>. They are therefore deduplicated, invalidated, garbage
/// collected, inspected in DevTools, persisted and hydrated like any other query, and can be updated
/// optimistically with <c>SetQueryData&lt;InfiniteData&lt;TParam, TPage&gt;&gt;</c>. Every infinite query
/// created for the same key shares the same pages. The instance observes the cache entry until it is
/// disposed.
/// </remarks>
/// <typeparam name="TParam">The type of the page parameter (offset, cursor, etc.).</typeparam>
/// <typeparam name="TPage">The type of a single page of data.</typeparam>
public sealed class InfiniteQuery<TParam, TPage> : IDisposable
{
    private readonly QueryClient _client;
    private readonly Func<TParam, CancellationToken, Task<TPage>> _fetcher;
    private readonly InfiniteQueryOptions<TParam, TPage> _options;
    private readonly ResolvedQueryOptions _resolved;
    private readonly QueryObserver<InfiniteData<TParam, TPage>> _observer;
    private readonly object _gate = new();
    private int _direction;
    private long _directionToken;
    private int _disposed;

    internal InfiniteQuery(
        QueryClient client,
        QueryKey key,
        Func<TParam, CancellationToken, Task<TPage>> fetcher,
        InfiniteQueryOptions<TParam, TPage> options,
        ResolvedQueryOptions resolved)
    {
        _client = client;
        Key = key;
        _fetcher = fetcher;
        _options = options;
        _resolved = resolved;
        _observer = client.Watch<InfiniteData<TParam, TPage>>(key);
        _observer.StateChanged += OnEntryChanged;
    }

    /// <summary>Gets the key under which the pages are cached.</summary>
    public QueryKey Key { get; }

    /// <summary>Raised whenever the query's state changes.</summary>
    public event Action<InfiniteQueryState<TParam, TPage>>? StateChanged;

    /// <summary>Gets an immutable snapshot of the query's current state.</summary>
    public InfiniteQueryState<TParam, TPage> CurrentState => Derive(EntryState(), Volatile.Read(ref _direction));

    /// <summary>Gets whether another page exists after the last fetched page.</summary>
    public bool HasNextPage => CurrentState.HasNextPage;

    /// <summary>Gets whether another page exists before the first fetched page.</summary>
    public bool HasPreviousPage => CurrentState.HasPreviousPage;

    /// <summary>
    /// Loads the query: serves fresh cached pages; otherwise fetches the first page (when there are none) or
    /// refetches every loaded page in order (when they are stale). Concurrent calls are deduplicated.
    /// </summary>
    public Task<InfiniteQueryState<TParam, TPage>> FetchAsync(CancellationToken cancellationToken = default) =>
        RunAsync(RefetchPagesAsync, FetchMode.IfStale, remember: true, direction: 0, cancellationToken);

    /// <summary>
    /// Refetches every loaded page in order (or the first page if none are loaded), cancelling any fetch in
    /// flight. Page parameters after the first are re-derived from the fresh pages, so cursors stay correct.
    /// </summary>
    public Task<InfiniteQueryState<TParam, TPage>> RefetchAsync(CancellationToken cancellationToken = default) =>
        RunAsync(RefetchPagesAsync, FetchMode.CancelAndRefetch, remember: true, direction: 0, cancellationToken);

    /// <summary>
    /// Fetches the next page and appends it (dropping the first page when <see cref="InfiniteQueryOptions{TParam, TPage}.MaxPages"/>
    /// is exceeded). Loads the first page if none are loaded. No-op if there is no next page. A background
    /// refetch in flight is cancelled in favor of the page the user asked for.
    /// </summary>
    public Task<InfiniteQueryState<TParam, TPage>> FetchNextPageAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentData();
        if (current.Pages.Count == 0)
        {
            return EnsureInitialAsync(cancellationToken);
        }

        if (!ResolveNext(current).HasValue)
        {
            return Task.FromResult(CurrentState);
        }

        return RunAsync(FetchNextAsync, FetchMode.CancelAndRefetch, remember: false, direction: 1, cancellationToken);
    }

    /// <summary>
    /// Fetches the previous page and prepends it (dropping the last page when <see cref="InfiniteQueryOptions{TParam, TPage}.MaxPages"/>
    /// is exceeded). Loads the first page if none are loaded. No-op if there is no previous page.
    /// </summary>
    public Task<InfiniteQueryState<TParam, TPage>> FetchPreviousPageAsync(CancellationToken cancellationToken = default)
    {
        if (_options.GetPreviousPageParam is null)
        {
            return Task.FromResult(CurrentState);
        }

        var current = CurrentData();
        if (current.Pages.Count == 0)
        {
            return EnsureInitialAsync(cancellationToken);
        }

        if (!ResolvePrevious(current).HasValue)
        {
            return Task.FromResult(CurrentState);
        }

        return RunAsync(FetchPreviousAsync, FetchMode.CancelAndRefetch, remember: false, direction: -1, cancellationToken);
    }

    private Task<InfiniteQueryState<TParam, TPage>> EnsureInitialAsync(CancellationToken cancellationToken) =>
        RunAsync(RefetchPagesAsync, FetchMode.Force, remember: true, direction: 0, cancellationToken);

    private async Task<InfiniteQueryState<TParam, TPage>> RunAsync(
        Func<CancellationToken, Task<InfiniteData<TParam, TPage>>> fetcher,
        FetchMode mode,
        bool remember,
        int direction,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        long token;
        lock (_gate)
        {
            token = ++_directionToken;
            _direction = direction;
        }

        try
        {
            await _client.Cache.WithEntry<InfiniteData<TParam, TPage>, Task<QueryState<InfiniteData<TParam, TPage>>>>(
                Key,
                entry => entry.TryFetchAsync(fetcher, _resolved, mode, remember, throwOnError: false, cancellationToken))
                .ConfigureAwait(false);
        }
        finally
        {
            var changed = false;
            lock (_gate)
            {
                if (_directionToken == token && _direction != 0)
                {
                    _direction = 0;
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }

        return CurrentState;
    }

    private async Task<InfiniteData<TParam, TPage>> RefetchPagesAsync(CancellationToken cancellationToken)
    {
        var current = CurrentData();
        if (current.Pages.Count == 0)
        {
            var first = await _fetcher(_options.InitialPageParam, cancellationToken).ConfigureAwait(false);
            return Build([first], [_options.InitialPageParam]);
        }

        var count = current.Pages.Count;
        var pages = new List<TPage>(count);
        var pageParams = new List<TParam>(count);
        var param = current.PageParams[0];
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                var next = _options.GetNextPageParam(pages[^1], pages, pageParams);
                if (!next.HasValue)
                {
                    break; // the list shrank on the server
                }

                param = next.Value;
            }

            pages.Add(await _fetcher(param, cancellationToken).ConfigureAwait(false));
            pageParams.Add(param);
        }

        return Build(pages, pageParams);
    }

    private async Task<InfiniteData<TParam, TPage>> FetchNextAsync(CancellationToken cancellationToken)
    {
        var current = CurrentData();
        if (current.Pages.Count == 0)
        {
            return await RefetchPagesAsync(cancellationToken).ConfigureAwait(false);
        }

        var next = ResolveNext(current);
        if (!next.HasValue)
        {
            return current;
        }

        var page = await _fetcher(next.Value, cancellationToken).ConfigureAwait(false);
        var pages = new List<TPage>(current.Pages) { page };
        var pageParams = new List<TParam>(current.PageParams) { next.Value };
        TrimToMaxPages(pages, pageParams, dropFromFront: true);
        return Build(pages, pageParams);
    }

    private async Task<InfiniteData<TParam, TPage>> FetchPreviousAsync(CancellationToken cancellationToken)
    {
        var current = CurrentData();
        if (current.Pages.Count == 0)
        {
            return await RefetchPagesAsync(cancellationToken).ConfigureAwait(false);
        }

        var previous = ResolvePrevious(current);
        if (!previous.HasValue)
        {
            return current;
        }

        var page = await _fetcher(previous.Value, cancellationToken).ConfigureAwait(false);
        var pages = new List<TPage>(current.Pages);
        var pageParams = new List<TParam>(current.PageParams);
        pages.Insert(0, page);
        pageParams.Insert(0, previous.Value);
        TrimToMaxPages(pages, pageParams, dropFromFront: false);
        return Build(pages, pageParams);
    }

    // Read the authoritative entry state (not the observer's copy, which may lag behind a notification that
    // another thread is still delivering), so page cursors are always computed from the latest pages.
    private QueryState<InfiniteData<TParam, TPage>> EntryState() =>
        _client.Cache.TryGetState<InfiniteData<TParam, TPage>>(Key, out var state) ? state : _observer.CurrentState;

    private InfiniteData<TParam, TPage> CurrentData() => EntryState().Data ?? InfiniteData<TParam, TPage>.Empty;

    private NextPageParam<TParam> ResolveNext(InfiniteData<TParam, TPage> data) =>
        data.Pages.Count == 0 ? default : _options.GetNextPageParam(data.Pages[^1], data.Pages, data.PageParams);

    private NextPageParam<TParam> ResolvePrevious(InfiniteData<TParam, TPage> data) =>
        data.Pages.Count == 0 || _options.GetPreviousPageParam is null
            ? default
            : _options.GetPreviousPageParam(data.Pages[0], data.Pages, data.PageParams);

    private InfiniteQueryState<TParam, TPage> Derive(QueryState<InfiniteData<TParam, TPage>> state, int direction)
    {
        var data = state.Data ?? InfiniteData<TParam, TPage>.Empty;
        return new InfiniteQueryState<TParam, TPage>
        {
            Status = state.Status,
            Data = data,
            Error = state.Error,
            FailureCount = state.FailureCount,
            HasNextPage = ResolveNext(data).HasValue,
            HasPreviousPage = ResolvePrevious(data).HasValue,
            IsFetchingNextPage = state.IsFetching && direction > 0,
            IsFetchingPreviousPage = state.IsFetching && direction < 0,
            DataUpdatedAt = state.DataUpdatedAt,
        };
    }

    private static InfiniteData<TParam, TPage> Build(IReadOnlyList<TPage> pages, IReadOnlyList<TParam> pageParams) =>
        new() { Pages = pages, PageParams = pageParams };

    private void TrimToMaxPages(List<TPage> pages, List<TParam> pageParams, bool dropFromFront)
    {
        if (_options.MaxPages is not { } max || max <= 0)
        {
            return;
        }

        while (pages.Count > max)
        {
            var index = dropFromFront ? 0 : pages.Count - 1;
            pages.RemoveAt(index);
            pageParams.RemoveAt(index);
        }
    }

    private void OnEntryChanged(QueryState<InfiniteData<TParam, TPage>> state) => RaiseStateChanged();

    private void RaiseStateChanged() => StateChanged?.Invoke(CurrentState);

    /// <summary>Stops observing the cached pages (they are garbage collected once no longer used).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _observer.StateChanged -= OnEntryChanged;
        _observer.Dispose();
        StateChanged = null;
    }
}
