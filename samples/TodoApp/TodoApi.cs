using DotNetQuery;

namespace TodoApp;

/// <summary>A single todo item.</summary>
public record Todo(int Id, string Title, bool Done);

/// <summary>Which todos to list.</summary>
public enum TodoFilter
{
    All,
    Open,
    Done,
}

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

    public async Task<List<Todo>> GetAllAsync(TodoFilter filter, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken); // simulate network latency
        lock (_todos)
        {
            return _todos
                .Where(t => filter switch
                {
                    TodoFilter.Open => !t.Done,
                    TodoFilter.Done => t.Done,
                    _ => true,
                })
                .ToList();
        }
    }

    public async Task<Todo> AddAsync(string title, CancellationToken cancellationToken)
    {
        await Task.Delay(250, cancellationToken);
        if (title.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The server rejected '{title}'.");
        }

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

/// <summary>
/// Every todo query, defined once: key + fetcher + options. Pages, components and mutations all use these,
/// so a key is never mistyped and always maps to the same data type.
/// </summary>
public static class TodoQueries
{
    /// <summary>The root of every todo key; invalidating it refreshes all todo lists.</summary>
    public static readonly QueryKey All = QueryKey.Create("todos");

    public static QueryDefinition<List<Todo>> List(TodoApi api, TodoFilter filter) =>
        new(All.Append(filter), ct => api.GetAllAsync(filter, ct), new QueryOptions { StaleTime = TimeSpan.FromSeconds(30) });
}
