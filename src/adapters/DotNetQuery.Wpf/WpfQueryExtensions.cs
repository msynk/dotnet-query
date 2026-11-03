using System.Windows.Threading;
using DotNetQuery.Mvvm;

namespace DotNetQuery.Wpf;

/// <summary>
/// WPF convenience factories for DotNetQuery MVVM bindings. Because
/// <see cref="ObservableQuery{T}"/> and <see cref="ObservableMutation{TVariables, TData}"/> capture the
/// current <see cref="SynchronizationContext"/> for UI-thread marshaling, create them on the WPF
/// Dispatcher thread - these helpers assert that.
/// </summary>
public static class WpfQueryExtensions
{
    /// <summary>
    /// Creates an <see cref="ObservableQuery{T}"/> for data binding. Must be called on the WPF UI thread.
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
    /// Creates an <see cref="ObservableQuery{T}"/> for a query definition. Must be called on the WPF UI thread.
    /// </summary>
    public static ObservableQuery<T> CreateObservableQuery<T>(this IQueryClient client, QueryDefinition<T> query)
    {
        EnsureUiThread();
        return new ObservableQuery<T>(client, query);
    }

    /// <summary>
    /// Creates an <see cref="ObservableInfiniteQuery{TParam, TPage}"/> for data binding (bind a "load more" control to
    /// its <c>FetchNextPageCommand</c>). Must be called on the WPF UI thread.
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
    /// the WPF UI thread.
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
        if (Dispatcher.FromThread(Thread.CurrentThread) is null && SynchronizationContext.Current is null)
        {
            throw new InvalidOperationException(
                "Create DotNetQuery MVVM bindings on the WPF UI thread so state changes marshal to the Dispatcher.");
        }
    }
}
