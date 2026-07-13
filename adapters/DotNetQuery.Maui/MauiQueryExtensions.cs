using DotNetQuery.Mvvm;
using Microsoft.Maui.ApplicationModel;

namespace DotNetQuery.Maui;

/// <summary>
/// .NET MAUI convenience factories for DotNetQuery MVVM bindings. Because
/// <see cref="ObservableQuery{T}"/> and <see cref="ObservableMutation{TVariables, TData}"/> capture the
/// current <see cref="SynchronizationContext"/> for UI-thread marshaling, create them on the MAUI main
/// thread - these helpers assert that.
/// </summary>
public static class MauiQueryExtensions
{
    /// <summary>
    /// Creates an <see cref="ObservableQuery{T}"/> for data binding. Must be called on the MAUI main thread.
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
    /// Creates an <see cref="ObservableMutation{TVariables, TData}"/> for data binding. Must be called on
    /// the MAUI main thread.
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
        if (!MainThread.IsMainThread && SynchronizationContext.Current is null)
        {
            throw new InvalidOperationException(
                "Create DotNetQuery MVVM bindings on the MAUI main thread so state changes marshal to the UI.");
        }
    }
}
