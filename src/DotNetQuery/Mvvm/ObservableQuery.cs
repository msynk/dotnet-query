using System.ComponentModel;
using System.Windows.Input;

namespace DotNetQuery.Mvvm;

/// <summary>
/// An <see cref="INotifyPropertyChanged"/> view of a query, for data binding in XAML UI frameworks
/// (WPF, WinUI, Avalonia, MAUI, Uno). Wraps a <see cref="QueryObserver{T}"/>, triggers the initial
/// fetch, and marshals state changes onto the UI thread captured at construction.
/// </summary>
/// <remarks>
/// State is applied on the captured UI thread together with its <see cref="PropertyChanged"/>
/// notifications, so bound properties are always consistent with the notification that announced them.
/// Call <see cref="ChangeQuery"/> when the query's inputs change (for example a selected item id) to switch
/// keys without recreating the binding.
/// </remarks>
/// <typeparam name="T">The type of data produced by the query.</typeparam>
public sealed class ObservableQuery<T> : INotifyPropertyChanged, IDisposable
{
    private readonly IQueryClient _client;
    private readonly SynchronizationContext? _syncContext;
    private QueryObserver<T> _observer;
    private QueryResult<T> _result;
    private QueryResult<T>? _previous;
    private readonly object _applyGate = new();
    private bool _keepPreviousData;
    private long _sequence;
    private long _applied;
    private int _disposed;

    /// <summary>
    /// Creates an observable query, subscribes to state changes, and starts the initial fetch.
    /// </summary>
    /// <param name="client">The query client.</param>
    /// <param name="key">The key identifying the query.</param>
    /// <param name="fetcher">Delegate that performs the fetch.</param>
    /// <param name="options">Optional per-query options.</param>
    public ObservableQuery(
        IQueryClient client,
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null)
        : this(client, new QueryDefinition<T>(key ?? throw new ArgumentNullException(nameof(key)), fetcher ?? throw new ArgumentNullException(nameof(fetcher)), options))
    {
    }

    /// <summary>Creates an observable query for a query definition and starts the initial fetch.</summary>
    public ObservableQuery(IQueryClient client, QueryDefinition<T> query)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(query);

        _syncContext = SynchronizationContext.Current;
        _observer = Subscribe(query);
        _result = _observer.CurrentResult;
        RefetchCommand = new AsyncCommand(_ => RefetchAsync());
        Fetch(query, forceRefetch: false);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the key of the query currently bound.</summary>
    public QueryKey Key => _result.Key ?? _observer.Key;

    /// <summary>Gets the most recent data, if any (placeholder data while <see cref="IsPlaceholderData"/>).</summary>
    public T? Data => _result.Data;

    /// <summary>Gets the most recent error, if any.</summary>
    public Exception? Error => _result.Error;

    /// <summary>Gets the lifecycle status of the query.</summary>
    public QueryStatus Status => _result.Status;

    /// <summary>Gets a value indicating whether the query is loading for the first time.</summary>
    public bool IsLoading => _result.IsLoading;

    /// <summary>Gets a value indicating whether the query has neither data nor an error yet. Bind first-load spinners to this.</summary>
    public bool IsPending => _result.IsPending;

    /// <summary>Gets a value indicating whether a fetch is currently in flight.</summary>
    public bool IsFetching => _result.IsFetching;

    /// <summary>Gets a value indicating whether the query has data and is refetching it in the background.</summary>
    public bool IsRefetching => _result.IsRefetching;

    /// <summary>Gets a value indicating whether the query has data from a successful fetch.</summary>
    public bool IsSuccess => _result.IsSuccess;

    /// <summary>Gets a value indicating whether the most recent fetch failed.</summary>
    public bool IsError => _result.IsError;

    /// <summary>Gets a value indicating whether the first fetch failed, so there is no data to show.</summary>
    public bool IsLoadingError => _result.IsLoadingError;

    /// <summary>Gets a value indicating whether a refetch failed while previous data is still shown.</summary>
    public bool IsRefetchError => _result.IsRefetchError;

    /// <summary>Gets a value indicating whether the query is paused waiting for connectivity.</summary>
    public bool IsPaused => _result.IsPaused;

    /// <summary>Gets a value indicating whether the query currently holds data.</summary>
    public bool HasData => _result.HasData;

    /// <summary>Gets a value indicating whether <see cref="Data"/> is the previous query's data shown while the current one loads.</summary>
    public bool IsPlaceholderData => _result.IsPlaceholderData;

    /// <summary>Gets when <see cref="Data"/> was last updated.</summary>
    public DateTimeOffset? DataUpdatedAt => _result.DataUpdatedAt;

    /// <summary>Gets the number of failed attempts in the current fetch cycle.</summary>
    public int FailureCount => _result.FailureCount;

    /// <summary>A command that refetches the query (for a refresh button or pull-to-refresh).</summary>
    public ICommand RefetchCommand { get; }

    /// <summary>Forces a refetch of the query, cancelling any fetch in flight.</summary>
    public Task RefetchAsync(CancellationToken cancellationToken = default) =>
        _client.RefetchQueryAsync(_observer.Key, cancellationToken);

    /// <summary>
    /// Switches the binding to another query (for example when a selected id changes) and fetches it.
    /// </summary>
    /// <param name="query">The query to bind to.</param>
    /// <param name="keepPreviousData">
    /// Keep exposing the current data (with <see cref="IsPlaceholderData"/> set) until the new query's first
    /// result arrives, instead of flashing an empty/loading state.
    /// </param>
    public void ChangeQuery(QueryDefinition<T> query, bool keepPreviousData = false)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (query.Key == _observer.Key)
        {
            Fetch(query, forceRefetch: false);
            return;
        }

        QueryObserver<T> previousObserver;
        lock (_applyGate)
        {
            _keepPreviousData = keepPreviousData;
            _previous = keepPreviousData ? _result : null;
            previousObserver = _observer;
            _observer = Subscribe(query);
        }

        Unsubscribe(previousObserver);
        Apply(_observer, _observer.CurrentState, Interlocked.Increment(ref _sequence));
        Fetch(query, forceRefetch: false);
    }

    private QueryObserver<T> Subscribe(QueryDefinition<T> query)
    {
        // The definition overload seeds initial data and carries the placeholder into CurrentResult.
        var observer = _client.Watch(query);
        observer.StateChanged += state => OnStateChanged(observer, state);
        return observer;
    }

    // Disposing the observer also detaches its handler.
    private static void Unsubscribe(QueryObserver<T> observer) => observer.Dispose();

    private void Fetch(QueryDefinition<T> query, bool forceRefetch) =>
        _ = ObserveAsync(_client.FetchAsync(query.Key, query.Fetcher, query.Options, forceRefetch));

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Failures are surfaced through Error/IsError; with ThrowOnError there is no awaiting caller to
            // rethrow to, so the exception must not go unobserved.
        }
    }

    private void OnStateChanged(QueryObserver<T> source, QueryState<T> state)
    {
        // Stamp each notification in delivery order; an older one applied late (a queued Post overtaken by an
        // inline apply on the UI thread) is then ignored instead of rolling the binding back.
        var sequence = Interlocked.Increment(ref _sequence);
        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
        {
            Apply(source, state, sequence);
        }
        else
        {
            _syncContext.Post(
                static s =>
                {
                    var (self, source, state, sequence) = ((ObservableQuery<T>, QueryObserver<T>, QueryState<T>, long))s!;
                    self.Apply(source, state, sequence);
                },
                (this, source, state, sequence));
        }
    }

    private void Apply(QueryObserver<T> source, QueryState<T> state, long sequence)
    {
        QueryResult<T> before;
        QueryResult<T> after;
        lock (_applyGate)
        {
            // Ignore late notifications from a query this binding has already switched away from, and
            // notifications older than one already applied.
            if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(source, _observer) || sequence <= _applied)
            {
                return;
            }

            _applied = sequence;
            before = _result;
            ApplyLocked(state);
            after = _result;
        }

        // Announce only what changed: re-raising Data on every status flip makes list controls rebuild (losing
        // scroll position and selection) even when the data instance is unchanged.
        foreach (var name in ChangedProperties(before, after))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static IEnumerable<string> ChangedProperties(QueryResult<T> before, QueryResult<T> after)
    {
        if (before.Key != after.Key)
        {
            yield return nameof(Key);
        }

        if (before.HasData != after.HasData || !SameData(before.Data, after.Data))
        {
            yield return nameof(Data);
        }

        if (!ReferenceEquals(before.Error, after.Error))
        {
            yield return nameof(Error);
        }

        if (before.Status != after.Status)
        {
            yield return nameof(Status);
        }

        foreach (var (name, changed) in new[]
        {
            (nameof(IsLoading), before.IsLoading != after.IsLoading),
            (nameof(IsPending), before.IsPending != after.IsPending),
            (nameof(IsFetching), before.IsFetching != after.IsFetching),
            (nameof(IsRefetching), before.IsRefetching != after.IsRefetching),
            (nameof(IsSuccess), before.IsSuccess != after.IsSuccess),
            (nameof(IsError), before.IsError != after.IsError),
            (nameof(IsLoadingError), before.IsLoadingError != after.IsLoadingError),
            (nameof(IsRefetchError), before.IsRefetchError != after.IsRefetchError),
            (nameof(IsPaused), before.IsPaused != after.IsPaused),
            (nameof(HasData), before.HasData != after.HasData),
            (nameof(IsPlaceholderData), before.IsPlaceholderData != after.IsPlaceholderData),
            (nameof(DataUpdatedAt), before.DataUpdatedAt != after.DataUpdatedAt),
            (nameof(FailureCount), before.FailureCount != after.FailureCount),
        })
        {
            if (changed)
            {
                yield return name;
            }
        }
    }

    // Reference identity for reference types (a new instance must reach the UI even if it compares equal);
    // value equality for value types.
    private static bool SameData(T? x, T? y) =>
        typeof(T).IsValueType ? EqualityComparer<T>.Default.Equals(x, y) : ReferenceEquals(x, y);

    private void ApplyLocked(QueryState<T> state)
    {
        // Previous data (keep-previous-data) takes precedence over the definition's placeholder.
        var result = _keepPreviousData && _previous is not null ? new QueryResult<T>(_observer.Key, state) : _observer.ToResult(state);

        if (_keepPreviousData && _previous is { } previous)
        {
            if (result.HasData || result.IsError)
            {
                _previous = null;
            }
            else
            {
                result = result.KeepPreviousData(previous);
            }
        }

        _result = result;
    }

    /// <summary>Unsubscribes from the query.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Unsubscribe(_observer);
        }
    }
}
