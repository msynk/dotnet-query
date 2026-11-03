using DotNetQuery.Mvvm;

namespace DotNetQuery.Avalonia;

/// <summary>
/// Avalonia convenience factories for DotNetQuery MVVM bindings. Because
/// <see cref="ObservableQuery{T}"/> and <see cref="ObservableMutation{TVariables, TData}"/> capture the
/// current <see cref="SynchronizationContext"/> for UI-thread marshaling, create them on the Avalonia
/// UI thread - these helpers assert that.
/// </summary>
public static class AvaloniaQueryExtensions
{
    /// <summary>
    /// Creates an <see cref="ObservableQuery{T}"/> for data binding. Must be called on the Avalonia UI thread.
    /// </summary>
    public static ObservableQuery<T> CreateObservableQuery<T>(
        this IQueryClient client,
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null)
    {
        EnsureUiThread();
        return new ObservableQuery<T>(client, key, fetcher, options);
    }

    /// <summary>
    /// Creates an <see cref="ObservableQuery{T}"/> for a query definition. Must be called on the Avalonia UI thread.
    /// </summary>
    public static ObservableQuery<T> CreateObservableQuery<T>(this IQueryClient client, QueryDefinition<T> query)
    {
        EnsureUiThread();
        return new ObservableQuery<T>(client, query);
    }

    /// <summary>
    /// Creates an <see cref="ObservableInfiniteQuery{TParam, TPage}"/> for data binding (bind a "load more" control to
    /// its <c>FetchNextPageCommand</c>). Must be called on the Avalonia UI thread.
    /// </summary>
    public static ObservableInfiniteQuery<TParam, TPage> CreateObservableInfiniteQuery<TParam, TPage>(
        this IQueryClient client,
        QueryKey key,
        Func<TParam, CancellationToken, Task<TPage>> fetcher,
        InfiniteQueryOptions<TParam, TPage> options)
    {
        EnsureUiThread();
        return new ObservableInfiniteQuery<TParam, TPage>(client, key, fetcher, options);
    }

    /// <summary>
    /// Creates an <see cref="ObservableMutation{TVariables, TData}"/> for data binding. Must be called on
    /// the Avalonia UI thread.
    /// </summary>
    public static ObservableMutation<TVariables, TData> CreateObservableMutation<TVariables, TData>(
        this IQueryClient client,
        Func<TVariables, CancellationToken, Task<TData>> mutationFn,
        MutationOptions<TVariables, TData>? options = null)
    {
        EnsureUiThread();
        return new ObservableMutation<TVariables, TData>(client, mutationFn, options);
    }

    private static void EnsureUiThread()
    {
        // Fully qualified to avoid ambiguity between this namespace and the Avalonia root namespace.
        if (!global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException(
                "Create DotNetQuery MVVM bindings on the Avalonia UI thread so state changes marshal to the Dispatcher.");
        }
    }
}
