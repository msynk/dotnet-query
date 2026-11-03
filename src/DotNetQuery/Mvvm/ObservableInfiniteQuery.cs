using System.ComponentModel;
using System.Windows.Input;

namespace DotNetQuery.Mvvm;

/// <summary>
/// An <see cref="INotifyPropertyChanged"/> view of an infinite (paginated) query, for data binding in XAML UI
/// frameworks: bind a list to <see cref="Pages"/> (or project the rows) and a "load more" button or an
/// end-of-list trigger to <see cref="FetchNextPageCommand"/>. Starts the first fetch on construction and marshals
/// state changes onto the UI thread captured at construction.
/// </summary>
/// <typeparam name="TParam">The type of the page parameter (offset, cursor, etc.).</typeparam>
/// <typeparam name="TPage">The type of a single page.</typeparam>
public sealed class ObservableInfiniteQuery<TParam, TPage> : INotifyPropertyChanged, IDisposable
{
    private readonly InfiniteQuery<TParam, TPage> _query;
    private readonly SynchronizationContext? _syncContext;
    private readonly object _applyGate = new();
    private readonly AsyncCommand _fetchNextPageCommand;
    private readonly AsyncCommand _fetchPreviousPageCommand;
    private InfiniteQueryState<TParam, TPage> _state;
    private long _sequence;
    private long _applied;
    private int _disposed;

    /// <summary>Creates an observable infinite query and starts fetching its first page.</summary>
    /// <param name="client">The query client.</param>
    /// <param name="key">The key identifying the infinite query.</param>
    /// <param name="fetcher">Fetches one page given its page parameter.</param>
    /// <param name="options">Pagination configuration.</param>
    public ObservableInfiniteQuery(
        IQueryClient client,
        QueryKey key,
        Func<TParam, CancellationToken, Task<TPage>> fetcher,
        InfiniteQueryOptions<TParam, TPage> options)
    {
        ArgumentNullException.ThrowIfNull(client);

        _syncContext = SynchronizationContext.Current;
        _query = client.CreateInfiniteQuery(key, fetcher, options);
        _state = _query.CurrentState;
        _fetchNextPageCommand = new AsyncCommand(_ => FetchNextPageAsync(), _ => HasNextPage && !IsFetchingNextPage);
        _fetchPreviousPageCommand = new AsyncCommand(_ => FetchPreviousPageAsync(), _ => HasPreviousPage && !IsFetchingPreviousPage);
        RefetchCommand = new AsyncCommand(_ => RefetchAsync());
        _query.StateChanged += OnStateChanged;
        _ = Observe(_query.FetchAsync());
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the key of the infinite query.</summary>
    public QueryKey Key => _query.Key;

    /// <summary>Gets the loaded pages and their parameters.</summary>
    public InfiniteData<TParam, TPage> Data => _state.Data;

    /// <summary>Gets the loaded pages, in order.</summary>
    public IReadOnlyList<TPage> Pages => _state.Data.Pages;

    /// <summary>Gets the number of loaded pages.</summary>
    public int PageCount => _state.PageCount;

    /// <summary>Gets the most recent error, if any.</summary>
    public Exception? Error => _state.Error;

    /// <summary>Gets the lifecycle status.</summary>
    public QueryStatus Status => _state.Status;

    /// <summary>Gets a value indicating whether the first page is loading.</summary>
    public bool IsLoading => _state.IsLoading;

    /// <summary>Gets a value indicating whether there is neither data nor an error yet.</summary>
    public bool IsPending => _state.IsPending;

    /// <summary>Gets a value indicating whether a fetch (first load or refetch) is in flight.</summary>
    public bool IsFetching => _state.IsFetching;

    /// <summary>Gets a value indicating whether pages were loaded successfully.</summary>
    public bool IsSuccess => _state.IsSuccess;

    /// <summary>Gets a value indicating whether the most recent fetch failed.</summary>
    public bool IsError => _state.IsError;

    /// <summary>Gets a value indicating whether another page exists after the last loaded one.</summary>
    public bool HasNextPage => _state.HasNextPage;

    /// <summary>Gets a value indicating whether another page exists before the first loaded one.</summary>
    public bool HasPreviousPage => _state.HasPreviousPage;

    /// <summary>Gets a value indicating whether the next page is being fetched.</summary>
    public bool IsFetchingNextPage => _state.IsFetchingNextPage;

    /// <summary>Gets a value indicating whether the previous page is being fetched.</summary>
    public bool IsFetchingPreviousPage => _state.IsFetchingPreviousPage;

    /// <summary>Gets when the pages were last updated.</summary>
    public DateTimeOffset? DataUpdatedAt => _state.DataUpdatedAt;

    /// <summary>Gets the number of failed attempts in the current fetch cycle.</summary>
    public int FailureCount => _state.FailureCount;

    /// <summary>A command that loads the next page; disabled when there is none or it is already loading.</summary>
    public ICommand FetchNextPageCommand => _fetchNextPageCommand;

    /// <summary>A command that loads the previous page; disabled when there is none or it is already loading.</summary>
    public ICommand FetchPreviousPageCommand => _fetchPreviousPageCommand;

    /// <summary>A command that refetches every loaded page.</summary>
    public ICommand RefetchCommand { get; }

    /// <summary>Loads the next page.</summary>
    public Task FetchNextPageAsync(CancellationToken cancellationToken = default) => _query.FetchNextPageAsync(cancellationToken);

    /// <summary>Loads the previous page.</summary>
    public Task FetchPreviousPageAsync(CancellationToken cancellationToken = default) => _query.FetchPreviousPageAsync(cancellationToken);

    /// <summary>Refetches every loaded page.</summary>
    public Task RefetchAsync(CancellationToken cancellationToken = default) => _query.RefetchAsync(cancellationToken);

    private static async Task Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Surfaced through Error/IsError.
        }
    }

    private void OnStateChanged(InfiniteQueryState<TParam, TPage> state)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
        {
            Apply(state, sequence);
        }
        else
        {
            _syncContext.Post(
                static s =>
                {
                    var (self, state, sequence) = ((ObservableInfiniteQuery<TParam, TPage>, InfiniteQueryState<TParam, TPage>, long))s!;
                    self.Apply(state, sequence);
                },
                (this, state, sequence));
        }
    }

    private void Apply(InfiniteQueryState<TParam, TPage> state, long sequence)
    {
        InfiniteQueryState<TParam, TPage> before;
        lock (_applyGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || sequence <= _applied)
            {
                return;
            }

            _applied = sequence;
            before = _state;
            _state = state;
        }

        // Announce only what changed, so list controls are not rebuilt by status flips.
        var dataChanged = !ReferenceEquals(before.Data, state.Data);
        foreach (var (name, changed) in new[]
        {
            (nameof(Data), dataChanged),
            (nameof(Pages), dataChanged),
            (nameof(PageCount), before.PageCount != state.PageCount),
            (nameof(Error), !ReferenceEquals(before.Error, state.Error)),
            (nameof(Status), before.Status != state.Status),
            (nameof(IsLoading), before.IsLoading != state.IsLoading),
            (nameof(IsPending), before.IsPending != state.IsPending),
            (nameof(IsFetching), before.IsFetching != state.IsFetching),
            (nameof(IsSuccess), before.IsSuccess != state.IsSuccess),
            (nameof(IsError), before.IsError != state.IsError),
            (nameof(HasNextPage), before.HasNextPage != state.HasNextPage),
            (nameof(HasPreviousPage), before.HasPreviousPage != state.HasPreviousPage),
            (nameof(IsFetchingNextPage), before.IsFetchingNextPage != state.IsFetchingNextPage),
            (nameof(IsFetchingPreviousPage), before.IsFetchingPreviousPage != state.IsFetchingPreviousPage),
            (nameof(DataUpdatedAt), before.DataUpdatedAt != state.DataUpdatedAt),
            (nameof(FailureCount), before.FailureCount != state.FailureCount),
        })
        {
            if (changed)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        _fetchNextPageCommand.RaiseCanExecuteChanged();
        _fetchPreviousPageCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Stops observing the infinite query.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _query.StateChanged -= OnStateChanged;
            _query.Dispose();
        }
    }
}
