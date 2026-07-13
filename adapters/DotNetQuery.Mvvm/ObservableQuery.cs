using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DotNetQuery.Mvvm;

/// <summary>
/// An <see cref="INotifyPropertyChanged"/> view of a query, for data binding in XAML UI frameworks
/// (WPF, WinUI, Avalonia, MAUI, Uno). Wraps a <see cref="QueryObserver{T}"/>, triggers the initial
/// fetch, and marshals change notifications onto the UI thread captured at construction.
/// </summary>
/// <typeparam name="T">The type of data produced by the query.</typeparam>
public sealed class ObservableQuery<T> : INotifyPropertyChanged, IDisposable
{
    private readonly IQueryClient _client;
    private readonly QueryKey _key;
    private readonly QueryObserver<T> _observer;
    private readonly SynchronizationContext? _syncContext;
    private QueryState<T> _state;

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
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _key = key ?? throw new ArgumentNullException(nameof(key));
        ArgumentNullException.ThrowIfNull(fetcher);

        _syncContext = SynchronizationContext.Current;
        _observer = client.Watch<T>(key);
        _state = _observer.CurrentState;
        _observer.StateChanged += OnStateChanged;
        _ = client.FetchAsync(key, fetcher, options);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the most recent data, if any.</summary>
    public T? Data => _state.Data;

    /// <summary>Gets the most recent error, if any.</summary>
    public Exception? Error => _state.Error;

    /// <summary>Gets the lifecycle status of the query.</summary>
    public QueryStatus Status => _state.Status;

    /// <summary>Gets a value indicating whether the query is loading for the first time.</summary>
    public bool IsLoading => _state.IsLoading;

    /// <summary>
    /// Gets a value indicating whether the query has no data yet (it is idle awaiting, or loading, its
    /// first result). Prefer binding first-load spinners to this rather than <see cref="IsLoading"/>,
    /// which is briefly <see langword="false"/> for a brand-new query before the loading transition.
    /// </summary>
    public bool IsPending => _state.IsPending;

    /// <summary>Gets a value indicating whether a fetch is currently in flight.</summary>
    public bool IsFetching => _state.IsFetching;

    /// <summary>Gets a value indicating whether the query has data from a successful fetch.</summary>
    public bool IsSuccess => _state.IsSuccess;

    /// <summary>Gets a value indicating whether the most recent fetch failed.</summary>
    public bool IsError => _state.IsError;

    /// <summary>Gets a value indicating whether the query currently holds data.</summary>
    public bool HasData => _state.HasData;

    /// <summary>Forces a refetch of the query.</summary>
    public Task RefetchAsync(CancellationToken cancellationToken = default) =>
        _client.RefetchQueryAsync(_key, cancellationToken);

    private void OnStateChanged(QueryState<T> state)
    {
        _state = state;
        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
        {
            RaiseAll();
        }
        else
        {
            _syncContext.Post(static self => ((ObservableQuery<T>)self!).RaiseAll(), this);
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(Data));
        Raise(nameof(Error));
        Raise(nameof(Status));
        Raise(nameof(IsLoading));
        Raise(nameof(IsPending));
        Raise(nameof(IsFetching));
        Raise(nameof(IsSuccess));
        Raise(nameof(IsError));
        Raise(nameof(HasData));
    }

    private void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Unsubscribes from the query.</summary>
    public void Dispose()
    {
        _observer.StateChanged -= OnStateChanged;
        _observer.Dispose();
    }
}
