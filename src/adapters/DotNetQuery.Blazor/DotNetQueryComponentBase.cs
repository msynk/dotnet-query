using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Components;

namespace DotNetQuery.Blazor;

/// <summary>
/// Optional base class for components that use queries and mutations from code, hook-style. Each
/// <c>Use*</c> call site owns one subscription: calling it again on later renders returns the same
/// subscription, and calling it with a different key replaces the subscription (refetching for the new
/// key). The component re-renders on every state change and all subscriptions are disposed with it.
/// </summary>
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
    private bool _disposed;

    /// <summary>Gets the query client.</summary>
    [Inject]
    protected IQueryClient QueryClient { get; set; } = default!;

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        var slot = SlotId(callerFilePath, callerLineNumber);
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

        var observer = QueryClient.Watch<T>(key);
        _slots[slot] = new QuerySlot<T>(observer, this) { Enabled = options?.Enabled ?? true };
        FetchInBackground(key, fetcher, options);
        return observer;
    }

    /// <summary>Subscribes this call site to a query definition (see <see cref="UseQuery{T}(QueryKey, Func{CancellationToken, Task{T}}, QueryOptions?, string, int)"/>).</summary>
    protected QueryObserver<T> UseQuery<T>(
        QueryDefinition<T> query,
        [CallerFilePath] string callerFilePath = "",
        [CallerLineNumber] int callerLineNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(query);
        return UseQuery(query.Key, query.Fetcher, query.Options, callerFilePath, callerLineNumber);
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

        var query = QueryClient.CreateInfiniteQuery(key, fetcher, options);
        _slots[slot] = new InfiniteSlot<TParam, TPage>(query, this);
        _ = ObserveAsync(query.FetchAsync());
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
        _ = ObserveAsync(QueryClient.FetchAsync(key, fetcher, options));

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

    private sealed class QuerySlot<T> : IDisposable
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

        private void OnChanged(QueryState<T> state) => _owner.Rerender();

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

    private sealed class InfiniteSlot<TParam, TPage> : IDisposable
    {
        private readonly DotNetQueryComponentBase _owner;

        public InfiniteSlot(InfiniteQuery<TParam, TPage> query, DotNetQueryComponentBase owner)
        {
            Query = query;
            _owner = owner;
            Query.StateChanged += OnChanged;
        }

        public InfiniteQuery<TParam, TPage> Query { get; }

        private void OnChanged(InfiniteQueryState<TParam, TPage> state) => _owner.Rerender();

        public void Dispose()
        {
            Query.StateChanged -= OnChanged;
            Query.Dispose();
        }
    }
}
