namespace TodoApp;

/// <summary>A single todo item.</summary>
public record Todo(int Id, string Title, bool Done);

/// <summary>
/// A fake in-memory "remote" API with artificial latency, standing in for a real REST/gRPC service.
/// </summary>
public sealed class TodoApi
{
    private readonly List<Todo> _todos =
    [
        new(1, "Learn DotNetQuery", false),
        new(2, "Wire up the cache", true),
        new(3, "Ship it", false),
    ];

    private int _nextId = 4;

    public async Task<List<Todo>> GetAllAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken); // simulate network latency
        lock (_todos)
        {
            return _todos.ToList();
        }
    }

    public async Task<Todo> AddAsync(string title, CancellationToken cancellationToken)
    {
        await Task.Delay(250, cancellationToken);
        lock (_todos)
        {
            var todo = new Todo(_nextId++, title, false);
            _todos.Add(todo);
            return todo;
        }
    }

    public async Task<Todo> ToggleAsync(int id, CancellationToken cancellationToken)
    {
        await Task.Delay(200, cancellationToken);
        lock (_todos)
        {
            var index = _todos.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                throw new InvalidOperationException($"Todo {id} not found.");
            }

            var toggled = _todos[index] with { Done = !_todos[index].Done };
            _todos[index] = toggled;
            return toggled;
        }
    }
}
