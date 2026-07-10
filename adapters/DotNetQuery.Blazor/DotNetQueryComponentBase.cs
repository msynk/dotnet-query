using Microsoft.AspNetCore.Components;

namespace DotNetQuery.Blazor;

/// <summary>
/// Optional base class for components that observe queries from code. Call <see cref="UseQuery{T}"/>
/// to subscribe; the component re-renders on state changes and all observers are disposed automatically.
/// </summary>
public abstract class DotNetQueryComponentBase : ComponentBase, IDisposable
{
    private readonly List<IDisposable> _subscriptions = [];

    [Inject]
    private IQueryClient QueryClient { get; set; } = default!;

    /// <summary>
    /// Subscribes to a query and triggers a fetch. The returned observer stays live for the lifetime of
    /// the component and re-renders it whenever the query state changes.
    /// </summary>
    protected QueryObserver<T> UseQuery<T>(
        QueryKey key,
        Func<CancellationToken, Task<T>> fetcher,
        QueryOptions? options = null)
    {
        var observer = QueryClient.Watch<T>(key);
        observer.StateChanged += _ => InvokeAsync(StateHasChanged);
        _subscriptions.Add(observer);
        _ = QueryClient.FetchAsync(key, fetcher, options);
        return observer;
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        GC.SuppressFinalize(this);
    }
}
