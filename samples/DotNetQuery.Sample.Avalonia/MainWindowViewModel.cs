using DotNetQuery.Avalonia;
using DotNetQuery.Mvvm;

namespace DotNetQuery.Sample.Avalonia;

/// <summary>
/// Exposes an <see cref="ObservableQuery{T}"/> created through the Avalonia adapter so the view can bind
/// straight to <c>Todos.Data</c>, <c>Todos.IsFetching</c>, <c>Todos.Status</c>, etc. Must be constructed
/// on the Avalonia UI thread (the adapter factory asserts this).
/// </summary>
public sealed class MainWindowViewModel
{
    private static readonly QueryKey TodosKey = QueryKey.Create("todos");

    private readonly IQueryClient _client;

    public MainWindowViewModel(IQueryClient client, TodoService service)
    {
        _client = client;
        Todos = client.CreateObservableQuery(TodosKey, service.GetTodosAsync);
    }

    /// <summary>The live query state as an INotifyPropertyChanged view.</summary>
    public ObservableQuery<List<string>> Todos { get; }

    /// <summary>Invalidates the query; the active observer refetches and the UI updates.</summary>
    public Task RefetchAsync() => _client.InvalidateQueriesAsync(TodosKey);
}
