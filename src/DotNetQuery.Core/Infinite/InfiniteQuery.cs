namespace DotNetQuery;

/// <summary>
/// A reusable, observable infinite (paginated) query. Supports forward and backward pagination via
/// <see cref="FetchNextPageAsync"/> and <see cref="FetchPreviousPageAsync"/>, exposing
/// <see cref="InfiniteQueryState{TParam, TPage}.HasNextPage"/> and
/// <see cref="InfiniteQueryState{TParam, TPage}.HasPreviousPage"/>.
/// </summary>
/// <typeparam name="TParam">The type of the page parameter (offset, cursor, etc.).</typeparam>
/// <typeparam name="TPage">The type of a single page of data.</typeparam>
public sealed class InfiniteQuery<TParam, TPage> : IDisposable, IInvalidatableInfiniteQuery
{
    private readonly Func<TParam, CancellationToken, Task<TPage>> _fetcher;
    private readonly InfiniteQueryOptions<TParam, TPage> _options;
    private readonly TimeProvider _timeProvider;
    private readonly Action<IInvalidatableInfiniteQuery>? _onDisposed;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _gate = new();
    private InfiniteQueryState<TParam, TPage> _state = InfiniteQueryState<TParam, TPage>.Idle;

    internal InfiniteQuery(
        QueryKey key,
        Func<TParam, CancellationToken, Task<TPage>> fetcher,
        InfiniteQueryOptions<TParam, TPage> options,
        TimeProvider timeProvider,
        Action<IInvalidatableInfiniteQuery>? onDisposed = null)
    {
        Key = key;
        _fetcher = fetcher;
        _options = options;
        _timeProvider = timeProvider;
        _onDisposed = onDisposed;
    }

    /// <summary>Gets the key identifying this infinite query, used for invalidation.</summary>
    public QueryKey Key { get; }

    /// <summary>Raised whenever the query's state changes.</summary>
    public event Action<InfiniteQueryState<TParam, TPage>>? StateChanged;

    /// <summary>Gets an immutable snapshot of the query's current state.</summary>
    public InfiniteQueryState<TParam, TPage> CurrentState
    {
        get { lock (_gate) { return _state; } }
    }

    /// <summary>Gets whether another page exists after the last fetched page.</summary>
    public bool HasNextPage => CurrentState.HasNextPage;

    /// <summary>Gets whether another page exists before the first fetched page.</summary>
    public bool HasPreviousPage => CurrentState.HasPreviousPage;

    /// <summary>Fetches the first page (using the initial page parameter), replacing any existing pages.</summary>
    public Task<InfiniteQueryState<TParam, TPage>> FetchAsync(CancellationToken cancellationToken = default) =>
        RunAsync(
            fetchingNext: false,
            fetchingPrevious: false,
            build: async ct =>
            {
                var page = await FetchPageAsync(_options.InitialPageParam, ct).ConfigureAwait(false);
                return BuildData([page], [_options.InitialPageParam]);
            },
            cancellationToken);

    /// <summary>Fetches the next page and appends it. No-op if there is no next page.</summary>
    public Task<InfiniteQueryState<TParam, TPage>> FetchNextPageAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentState.Data;
        if (current.Pages.Count == 0)
        {
            return FetchAsync(cancellationToken);
        }

        var next = _options.GetNextPageParam(current.Pages[^1], current.Pages, current.PageParams);
        if (!next.HasValue)
        {
            return Task.FromResult(CurrentState);
        }

        return RunAsync(
            fetchingNext: true,
            fetchingPrevious: false,
            build: async ct =>
            {
                var page = await FetchPageAsync(next.Value, ct).ConfigureAwait(false);
                var pages = new List<TPage>(current.Pages) { page };
                var pageParams = new List<TParam>(current.PageParams) { next.Value };
                TrimToMaxPages(pages, pageParams, dropFromFront: true);
                return BuildData(pages, pageParams);
            },
            cancellationToken);
    }

    /// <summary>Fetches the previous page and prepends it. No-op if there is no previous page.</summary>
    public Task<InfiniteQueryState<TParam, TPage>> FetchPreviousPageAsync(CancellationToken cancellationToken = default)
    {
        if (_options.GetPreviousPageParam is null)
        {
            return Task.FromResult(CurrentState);
        }

        var current = CurrentState.Data;
        if (current.Pages.Count == 0)
        {
            return FetchAsync(cancellationToken);
        }

        var previous = _options.GetPreviousPageParam(current.Pages[0], current.Pages, current.PageParams);
        if (!previous.HasValue)
        {
            return Task.FromResult(CurrentState);
        }

        return RunAsync(
            fetchingNext: false,
            fetchingPrevious: true,
            build: async ct =>
            {
                var page = await FetchPageAsync(previous.Value, ct).ConfigureAwait(false);
                var pages = new List<TPage>(current.Pages);
                var pageParams = new List<TParam>(current.PageParams);
                pages.Insert(0, page);
                pageParams.Insert(0, previous.Value);
                TrimToMaxPages(pages, pageParams, dropFromFront: false);
                return BuildData(pages, pageParams);
            },
            cancellationToken);
    }

    /// <summary>Refetches every currently loaded page in order, rebuilding the accumulated data.</summary>
    public Task<InfiniteQueryState<TParam, TPage>> RefetchAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentState.Data;
        if (current.PageParams.Count == 0)
        {
            return FetchAsync(cancellationToken);
        }

        return RunAsync(
            fetchingNext: false,
            fetchingPrevious: false,
            build: async ct =>
            {
                var pages = new List<TPage>(current.PageParams.Count);
                foreach (var param in current.PageParams)
                {
                    pages.Add(await FetchPageAsync(param, ct).ConfigureAwait(false));
                }

                return BuildData(pages, current.PageParams);
            },
            cancellationToken);
    }

    private async Task<InfiniteQueryState<TParam, TPage>> RunAsync(
        bool fetchingNext,
        bool fetchingPrevious,
        Func<CancellationToken, Task<InfiniteData<TParam, TPage>>> build,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(previous => previous with
            {
                Status = previous.Data.Pages.Count > 0 ? QueryStatus.Refetching : QueryStatus.Loading,
                IsFetchingNextPage = fetchingNext,
                IsFetchingPreviousPage = fetchingPrevious,
                Error = null,
            });

            var data = await build(cancellationToken).ConfigureAwait(false);

            SetState(_ => new InfiniteQueryState<TParam, TPage>
            {
                Status = QueryStatus.Success,
                Data = data,
                Error = null,
                HasNextPage = ResolveHasNext(data),
                HasPreviousPage = ResolveHasPrevious(data),
                IsFetchingNextPage = false,
                IsFetchingPreviousPage = false,
                DataUpdatedAt = _timeProvider.GetUtcNow(),
            });

            return CurrentState;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(previous => previous with { IsFetchingNextPage = false, IsFetchingPreviousPage = false });
            throw;
        }
        catch (Exception ex)
        {
            SetState(previous => previous with
            {
                Status = QueryStatus.Error,
                Error = ex,
                IsFetchingNextPage = false,
                IsFetchingPreviousPage = false,
            });
            return CurrentState;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<TPage> FetchPageAsync(TParam param, CancellationToken cancellationToken)
    {
        var retryDelay = _options.RetryDelay ?? RetryPolicy.Constant(TimeSpan.Zero);
        var attempt = 0;
        while (true)
        {
            try
            {
                return await _fetcher(param, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                if (attempt > _options.RetryCount)
                {
                    throw;
                }

                var delay = retryDelay(attempt - 1, ex);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static InfiniteData<TParam, TPage> BuildData(IReadOnlyList<TPage> pages, IReadOnlyList<TParam> pageParams) =>
        new() { Pages = pages, PageParams = pageParams };

    private bool ResolveHasNext(InfiniteData<TParam, TPage> data) =>
        data.Pages.Count > 0 && _options.GetNextPageParam(data.Pages[^1], data.Pages, data.PageParams).HasValue;

    private bool ResolveHasPrevious(InfiniteData<TParam, TPage> data) =>
        _options.GetPreviousPageParam is not null
        && data.Pages.Count > 0
        && _options.GetPreviousPageParam(data.Pages[0], data.Pages, data.PageParams).HasValue;

    private void TrimToMaxPages(List<TPage> pages, List<TParam> pageParams, bool dropFromFront)
    {
        if (_options.MaxPages is not { } max || pages.Count <= max)
        {
            return;
        }

        var index = dropFromFront ? 0 : pages.Count - 1;
        pages.RemoveAt(index);
        pageParams.RemoveAt(index);
    }

    private void SetState(Func<InfiniteQueryState<TParam, TPage>, InfiniteQueryState<TParam, TPage>> mutate)
    {
        InfiniteQueryState<TParam, TPage> newState;
        lock (_gate)
        {
            newState = mutate(_state);
            _state = newState;
        }

        StateChanged?.Invoke(newState);
    }

    Task IInvalidatableInfiniteQuery.RefetchAllAsync(CancellationToken cancellationToken) => RefetchAsync(cancellationToken);

    /// <summary>Unregisters from the client and releases the internal synchronization primitive.</summary>
    public void Dispose()
    {
        _onDisposed?.Invoke(this);
        _mutex.Dispose();
    }
}
