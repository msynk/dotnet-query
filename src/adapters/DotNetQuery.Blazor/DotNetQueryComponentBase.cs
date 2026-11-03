using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetQuery.Blazor;

/// <summary>
/// Optional base class for components that use queries and mutations from code, hook-style. Each
/// <c>Use*</c> call site owns one subscription: calling it again on later renders returns the same
/// subscription, and calling it with a different key replaces the subscription (refetching for the new
/// key). The component re-renders on every state change and all subscriptions are disposed with it.
/// </summary>
/// <remarks>
/// During prerendering the component waits for the fetches its <c>Use*</c> calls started (so the prerendered
/// HTML contains the data) and persists the results into the page; the interactive render hydrates them with
/// their original timestamps instead of fetching again - the same behavior as the <c>&lt;Query&gt;</c>
/// component. Override <see cref="PersistQueryState"/> to opt out for data that must not be embedded in the page.
/// </remarks>
/// <example>
/// <code>
/// @inherits DotNetQueryComponentBase
///
/// @code {
///     [Parameter] public int Id { get; set; }
///     private QueryObserver&lt;Todo&gt; _todo = default!;
///     private Mutation&lt;Todo, Todo&gt; _save = default!;
///
///     protected override void OnParametersSet()
///     {
///         _todo = UseQuery(TodoQueries.ById(Api, Id));            // re-keys automatically when Id changes
///         _save = UseMutation&lt;Todo, Todo&gt;(Api.SaveAsync, new() { Invalidates = [TodoQueries.All] });
///     }
/// }
/// </code>
/// </example>
public abstract class DotNetQueryComponentBase : ComponentBase, IDisposable
{
    private readonly Dictionary<string, IDisposable> _slots = new(StringComparer.Ordinal);
    private readonly List<Task> _pendingFetches = [];
    private PersistentComponentState? _applicationState;
    private PersistingComponentStateSubscription? _persisting;
    private bool _applicationStateResolved;
    private bool _guardResolved;
    private bool _disposed;

    private IQueryClient? _queryClient;

    /// <summary>
    /// Gets (or replaces) the query client: the one supplied by a surrounding <see cref="QueryClientProvider"/>, else
    /// the registered one.
    /// </summary>
    protected IQueryClient QueryClient
    {
        get => _queryClient ?? CascadedClient ?? InjectedClient;
        set => _queryClient = value;
    }

    [Inject]
    private IQueryClient InjectedClient { get; set; } = default!;

    [CascadingParameter]
    private IQueryClient? CascadedClient { get; set; }

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>
    /// Whether data fetched by <c>UseQuery</c>/<c>UseInfiniteQuery</c> while prerendering is persisted into the
    /// page and used to hydrate the interactive render (default <see langword="true"/>). Override to return
    /// <see langword="false"/> for data that must not be embedded in the HTML or cannot be serialized to JSON.
    /// </summary>
    protected virtual bool PersistQueryState => true;

    /// <inheritdoc />
    /// <remarks>
    /// Completes once the fetches started by <c>Use*</c> calls during this parameter update have settled, so
    /// prerendering (and streaming rendering) includes their data. Interactive renders are not delayed: the
    /// component has already rendered its loading state and re-renders as the data arrives.
    /// </remarks>
    public override async Task SetParametersAsync(ParameterView parameters)
    {
        if (!_guardResolved)
        {
            // Activates the user-change guard when ResetOnUserChange() registered one.
            _guardResolved = true;
            _ = Services?.GetService<QueryAuthenticationGuard>();
        }

        await base.SetParametersAsync(parameters);
        if (_pendingFetches.Count > 0)
        {
            var pending = _pendingFetches.ToArray();
            _pendingFetches.Clear();
            await Task.WhenAll(pending);
        }
    }

    /// <summary>
    /// Subscribes this call site to a query and fetches it (serving fresh cached data). Subsequent calls
    /// with the same key return the same observer without refetching; a new key replaces the subscription.
    /// </summary>
    /// <param name="key">The key identifying the query.</param>
    /// <param name="fetcher">Delegate that performs the fetch.</param>
    /// <param name="options">Optional per-query options.</param>
    /// <param name="callerFilePath">Compiler-supplied call-site identity. Do not pass.</param>
    /// <param name="callerLineNumber">Compiler-supplied call-site identity. Do not pass.</param>
    protected QueryObserver<T> UseQuery<T>(
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fetcher);
        return UseQueryCore(new QueryDefinition<T>(key, fetcher, options), SlotId(callerFilePath, callerLineNumber));
    }

    /// <summary>
    /// Subscribes this call site to a query definition (see <see cref="UseQuery{T}(QueryKey, Func{CancellationToken, Task{T}}, QueryOptions?, string, int)"/>),
    /// seeding its <see cref="QueryDefinition{T}.InitialData"/> and exposing its
    /// <see cref="QueryDefinition{T}.PlaceholderData"/> through <see cref="QueryObserver{T}.CurrentResult"/>.
    /// </summary>
    protected QueryObserver<T> UseQuery<T>(
        QueryDefinition<T> query,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(query);
        return UseQueryCore(query, SlotId(callerFilePath, callerLineNumber));
    }

    /// <summary>
    /// Subscribes this call site to a query definition through a selector: the observer's data is
    /// <paramref name="select"/>(cached data), and the component re-renders only when one of the
    /// <paramref name="notifyOn"/> properties changes - <see cref="QueryStateProperties.Data"/> meaning the selected
    /// value changed (by structural equality). The selector of the first call is kept; a new key replaces the
    /// subscription.
    /// </summary>
    /// <example>
    /// <code>
    /// // Re-renders only when the open count changes, not on every background refetch.
    /// _open = UseQuery(TodoQueries.List(Api), todos => todos.Count(t => !t.Done), QueryStateProperties.Data);
    /// </code>
    /// </example>
    protected QueryObserver<TResult> UseQuery<T, TResult>(
        QueryDefinition<T> query,
        Func<T, TResult> select,
        QueryStateProperties notifyOn = QueryStateProperties.All,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(select);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var slot = SlotId(callerFilePath, callerLineNumber);
        var enabled = query.Options?.Enabled ?? true;
        if (_slots.TryGetValue(slot, out var existing))
        {
            if (existing is SelectSlot<T, TResult> current && current.Observer.Key == query.Key)
            {
                if (current.Enabled != enabled)
                {
                    current.Enabled = enabled;
                    FetchInBackground(query.Key, query.Fetcher, query.Options);
                }

                return current.Observer;
            }

            existing.Dispose();
            _slots.Remove(slot);
        }

        Hydrate<T>(query.Key);
        var observer = QueryClient.Watch(query, select, notifyOn);
        _slots[slot] = new SelectSlot<T, TResult>(observer, query.Key, this) { Enabled = enabled };
        FetchInBackground(query.Key, query.Fetcher, query.Options);
        return observer;
    }

    /// <summary>
    /// Subscribes this call site to a dynamic list of queries (TanStack's <c>useQueries</c>): each is fetched in
    /// parallel and observed; on later renders, queries that left the list are unsubscribed and new ones added, so the
    /// returned observers always match <paramref name="queries"/> in order.
    /// </summary>
    /// <example><c>_users = UseQueries(SelectedIds.Select(id =&gt; UserQueries.ById(Api, id)).ToList());</c></example>
    protected IReadOnlyList<QueryObserver<T>> UseQueries<T>(
        IReadOnlyList<QueryDefinition<T>> queries,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var slotId = SlotId(callerFilePath, callerLineNumber);
        if (!_slots.TryGetValue(slotId, out var existing) || existing is not QueriesSlot<T> slot)
        {
            existing?.Dispose();
            slot = new QueriesSlot<T>(this);
            _slots[slotId] = slot;
        }

        return slot.Reconcile(queries);
    }

    private QueryObserver<T> UseQueryCore<T>(QueryDefinition<T> query, string slot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var (key, fetcher, options) = (query.Key, query.Fetcher, query.Options);

        if (_slots.TryGetValue(slot, out var existing))
        {
            if (existing is QuerySlot<T> current && current.Observer.Key == key)
            {
                if (current.Enabled != (options?.Enabled ?? true))
                {
                    current.Enabled = options?.Enabled ?? true;
                    FetchInBackground(key, fetcher, options);
                }

                return current.Observer;
            }

            existing.Dispose();
            _slots.Remove(slot);
        }

        Hydrate<T>(key);
        var observer = QueryClient.Watch(query);
        _slots[slot] = new QuerySlot<T>(observer, this) { Enabled = options?.Enabled ?? true };
        FetchInBackground(key, fetcher, options);
        return observer;
    }

    /// <summary>
    /// Creates (once per call site) a mutation whose state changes re-render this component. Later calls
    /// return the same instance, so the mutation function and options are taken from the first call.
    /// </summary>
    protected Mutation<TVariables, TData> UseMutation<TVariables, TData>(
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options = null,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(mutationFn);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var slot = SlotId(callerFilePath, callerLineNumber);
        if (_slots.TryGetValue(slot, out var existing) && existing is MutationSlot<TVariables, TData> current)
        {
            return current.Mutation;
        }

        existing?.Dispose();
        var mutation = QueryClient.CreateMutation(mutationFn, options);
        _slots[slot] = new MutationSlot<TVariables, TData>(mutation, this);
        return mutation;
    }

    /// <summary>
    /// Subscribes this call site to an infinite query and loads it. Subsequent calls with the same key return
    /// the same instance; a new key replaces it.
    /// </summary>
    protected InfiniteQuery<TParam, TPage> UseInfiniteQuery<TParam, TPage>(
        QueryKey key,
        Func<TParam, CancellationToken, Task<TPage>> fetcher,
        InfiniteQueryOptions<TParam, TPage> options,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var slot = SlotId(callerFilePath, callerLineNumber);
        if (_slots.TryGetValue(slot, out var existing))
        {
            if (existing is InfiniteSlot<TParam, TPage> current && current.Query.Key == key)
            {
                return current.Query;
            }

            existing.Dispose();
            _slots.Remove(slot);
        }

        Hydrate<InfiniteData<TParam, TPage>>(key);
        var query = QueryClient.CreateInfiniteQuery(key, fetcher, options);
        _slots[slot] = new InfiniteSlot<TParam, TPage>(query, this);
        Track(ObserveAsync(query.FetchAsync()));
        return query;
    }

    /// <summary>Releases every subscription created by the <c>Use*</c> methods.</summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            _persisting?.Dispose();
            _persisting = null;

            foreach (var slot in _slots.Values)
            {
                slot.Dispose();
            }

            _slots.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private static string SlotId(string file, int line) => string.Concat(file, ":", line.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void FetchInBackground<T>(QueryKey key, Func<CancellationToken, Task<T>> fetcher, QueryOptions? options) =>
        Track(ObserveAsync(QueryClient.FetchAsync(key, fetcher, options)));

    // Remembered until the next SetParametersAsync awaits them; ObserveAsync never faults.
    private void Track(Task fetch)
    {
        if (!fetch.IsCompleted)
        {
            _pendingFetches.RemoveAll(static t => t.IsCompleted);
            _pendingFetches.Add(fetch);
        }
    }

    private PersistentComponentState? ApplicationState
    {
        get
        {
            if (!_applicationStateResolved)
            {
                _applicationStateResolved = true;
                if (PersistQueryState && Services?.GetService<PersistentComponentState>() is { } state)
                {
                    _applicationState = state;
                    _persisting = state.RegisterOnPersisting(PersistSlots);
                }
            }

            return _applicationState;
        }
    }

    private void Hydrate<T>(QueryKey key)
    {
        if (ApplicationState is { } state)
        {
            QueryStateHydration.TryHydrate<T>(state, QueryClient, key);
        }
    }

    private Task PersistSlots()
    {
        if (_applicationState is { } state)
        {
            foreach (var slot in _slots.Values)
            {
                (slot as IPersistableSlot)?.Persist(state, QueryClient);
            }
        }

        return Task.CompletedTask;
    }

    // Surfaces ThrowOnError failures to the nearest ErrorBoundary instead of leaving them unobserved.
    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (!_disposed)
        {
            await DispatchExceptionAsync(ex);
        }
        catch (Exception)
        {
            // Disposed: nobody left to report to.
        }
    }

    private void Rerender()
    {
        if (!_disposed)
        {
            _ = RerenderAsync();
        }
    }

    private async Task RerenderAsync()
    {
        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // The renderer is gone (circuit closed).
        }
    }

    private interface IPersistableSlot
    {
        void Persist(PersistentComponentState state, IQueryClient client);
    }

    private sealed class QuerySlot<T> : IDisposable, IPersistableSlot
    {
        private readonly DotNetQueryComponentBase _owner;

        public QuerySlot(QueryObserver<T> observer, DotNetQueryComponentBase owner)
        {
            Observer = observer;
            _owner = owner;
            Observer.StateChanged += OnChanged;
        }

        public QueryObserver<T> Observer { get; }

        public bool Enabled { get; set; }

        public void Persist(PersistentComponentState state, IQueryClient client) => QueryStateHydration.Persist<T>(state, client, Observer.Key);

        private void OnChanged(QueryState<T> state) => _owner.Rerender();

        public void Dispose()
        {
            Observer.StateChanged -= OnChanged;
            Observer.Dispose();
        }
    }

    private sealed class QueriesSlot<T>(DotNetQueryComponentBase owner) : IDisposable, IPersistableSlot
    {
        private readonly Dictionary<QueryKey, Item> _items = [];

        public IReadOnlyList<QueryObserver<T>> Reconcile(IReadOnlyList<QueryDefinition<T>> queries)
        {
            var wanted = new HashSet<QueryKey>(queries.Select(q => q.Key));
            foreach (var key in _items.Keys.Where(k => !wanted.Contains(k)).ToArray())
            {
                Release(_items[key].Observer);
                _items.Remove(key);
            }

            var result = new List<QueryObserver<T>>(queries.Count);
            foreach (var query in queries)
            {
                var enabled = query.Options?.Enabled ?? true;
                if (!_items.TryGetValue(query.Key, out var item))
                {
                    owner.Hydrate<T>(query.Key);
                    var observer = owner.QueryClient.Watch(query);
                    observer.StateChanged += OnChanged;
                    _items[query.Key] = item = new Item(observer) { Enabled = enabled };
                    owner.FetchInBackground(query.Key, query.Fetcher, query.Options);
                }
                else if (item.Enabled != enabled)
                {
                    // Like UseQuery: a query whose Enabled flips (a dependent query becoming ready) is fetched with its
                    // new options, so it loads now and later refetches use them.
                    item.Enabled = enabled;
                    owner.FetchInBackground(query.Key, query.Fetcher, query.Options);
                }

                result.Add(item.Observer);
            }

            return result;
        }

        public void Persist(PersistentComponentState state, IQueryClient client)
        {
            foreach (var key in _items.Keys)
            {
                QueryStateHydration.Persist<T>(state, client, key);
            }
        }

        private void OnChanged(QueryState<T> state) => owner.Rerender();

        private void Release(QueryObserver<T> observer)
        {
            observer.StateChanged -= OnChanged;
            observer.Dispose();
        }

        public void Dispose()
        {
            foreach (var item in _items.Values)
            {
                Release(item.Observer);
            }

            _items.Clear();
        }

        private sealed class Item(QueryObserver<T> observer)
        {
            public QueryObserver<T> Observer { get; } = observer;

            public bool Enabled { get; set; }
        }
    }

    private sealed class SelectSlot<T, TResult> : IDisposable, IPersistableSlot
    {
        private readonly DotNetQueryComponentBase _owner;
        private readonly QueryKey _key;

        public SelectSlot(QueryObserver<TResult> observer, QueryKey key, DotNetQueryComponentBase owner)
        {
            Observer = observer;
            _key = key;
            _owner = owner;
            Observer.StateChanged += OnChanged;
        }

        public QueryObserver<TResult> Observer { get; }

        public bool Enabled { get; set; }

        // Persist the source query's data (type T), which the interactive render hydrates before selecting.
        public void Persist(PersistentComponentState state, IQueryClient client) => QueryStateHydration.Persist<T>(state, client, _key);

        private void OnChanged(QueryState<TResult> state) => _owner.Rerender();

        public void Dispose()
        {
            Observer.StateChanged -= OnChanged;
            Observer.Dispose();
        }
    }

    private sealed class MutationSlot<TVariables, TData> : IDisposable
    {
        private readonly DotNetQueryComponentBase _owner;

        public MutationSlot(Mutation<TVariables, TData> mutation, DotNetQueryComponentBase owner)
        {
            Mutation = mutation;
            _owner = owner;
            Mutation.StateChanged += OnChanged;
        }

        public Mutation<TVariables, TData> Mutation { get; }

        private void OnChanged(MutationState<TVariables, TData> state) => _owner.Rerender();

        public void Dispose() => Mutation.StateChanged -= OnChanged;
    }

    private sealed class InfiniteSlot<TParam, TPage> : IDisposable, IPersistableSlot
    {
        private readonly DotNetQueryComponentBase _owner;

        public InfiniteSlot(InfiniteQuery<TParam, TPage> query, DotNetQueryComponentBase owner)
        {
            Query = query;
            _owner = owner;
            Query.StateChanged += OnChanged;
        }

        public InfiniteQuery<TParam, TPage> Query { get; }

        public void Persist(PersistentComponentState state, IQueryClient client) =>
            QueryStateHydration.Persist<InfiniteData<TParam, TPage>>(state, client, Query.Key);

        private void OnChanged(InfiniteQueryState<TParam, TPage> state) => _owner.Rerender();

        public void Dispose()
        {
            Query.StateChanged -= OnChanged;
            Query.Dispose();
        }
    }
}
