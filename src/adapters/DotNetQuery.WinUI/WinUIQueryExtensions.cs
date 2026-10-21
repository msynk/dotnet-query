using DotNetQuery.Mvvm;
using Microsoft.UI.Dispatching;

namespace DotNetQuery.WinUI;

/// <summary>
/// WinUI 3 convenience factories for DotNetQuery MVVM bindings. Because
/// <see cref="ObservableQuery{T}"/> and <see cref="ObservableMutation{TVariables, TData}"/> capture the
/// current <see cref="SynchronizationContext"/> for UI-thread marshaling, create them on the WinUI
/// UI thread - these helpers assert that.
/// </summary>
public static class WinUIQueryExtensions
{
    /// <summary>
    /// Creates an <see cref="ObservableQuery{T}"/> for data binding. Must be called on the WinUI UI thread.
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
    /// Creates an <see cref="ObservableQuery{T}"/> for a query definition. Must be called on the WinUI UI thread.
    /// </summary>
    public static ObservableQuery<T> CreateObservableQuery<T>(this IQueryClient client, QueryDefinition<T> query)
    {
        EnsureUiThread();
        return new ObservableQuery<T>(client, query);
    }

    /// <summary>
    /// Creates an <see cref="ObservableMutation{TVariables, TData}"/> for data binding. Must be called on
    /// the WinUI UI thread.
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
        if (DispatcherQueue.GetForCurrentThread() is null && SynchronizationContext.Current is null)
        {
            throw new InvalidOperationException(
                "Create DotNetQuery MVVM bindings on the WinUI UI thread so state changes marshal to the DispatcherQueue.");
        }
    }
}
