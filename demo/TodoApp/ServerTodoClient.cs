using TodoApp.Client;

namespace TodoApp;

/// <summary>
/// The server's <see cref="ITodoClient"/>: used while prerendering the WebAssembly/Auto pages (and by Auto pages while
/// they run on the server), calling the data layer directly instead of going through HTTP.
/// </summary>
public sealed class ServerTodoClient(TodoApi api) : ITodoClient
{
    public async Task<List<TodoItem>> GetAllAsync(CancellationToken cancellationToken) =>
        (await api.GetAllAsync(TodoFilter.All, cancellationToken)).Select(t => new TodoItem(t.Id, t.Title, t.Done)).ToList();

    public async Task<TodoItem> AddAsync(string title, CancellationToken cancellationToken)
    {
        var todo = await api.AddAsync(title, cancellationToken);
        return new TodoItem(todo.Id, todo.Title, todo.Done);
    }
}
