using System.Text.Json.Serialization;
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

    public async Task<Todo> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken);
        lock (_todos)
        {
            return _todos.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException($"Todo {id} not found.");
        }
    }

    public async Task<Todo> UpdateAsync(Todo todo, CancellationToken cancellationToken)
    {
        await Task.Delay(200, cancellationToken);
        if (string.IsNullOrWhiteSpace(todo.Title))
        {
            throw new InvalidOperationException("A todo needs a title.");
        }

        lock (_todos)
        {
            var index = _todos.FindIndex(t => t.Id == todo.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Todo {todo.Id} not found.");
            }

            _todos[index] = todo;
            return todo;
        }
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken);
        lock (_todos)
        {
            _todos.RemoveAll(t => t.Id == id);
        }
    }

    /// <summary>A large, generated, read-only dataset (10,000 rows) served page by page.</summary>
    public async Task<RowPage> GetRowsAsync(int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(120, cancellationToken);
        const int Total = 10_000;
        var rows = Enumerable.Range(offset, Math.Max(0, Math.Min(count, Total - offset)))
            .Select(i => new Row(i, "Row " + i.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), (i * 7919) % 1000))
            .ToArray();
        return new RowPage(offset, rows, Total);
    }

    /// <summary>Per-tenant projects (the tenant is part of every key, so tenants never share cached data).</summary>
    public async Task<List<string>> GetProjectsAsync(string tenant, CancellationToken cancellationToken)
    {
        await Task.Delay(150, cancellationToken);
        return tenant switch
        {
            "contoso" => ["Contoso website", "Contoso mobile app", "Contoso data platform"],
            "fabrikam" => ["Fabrikam ERP migration", "Fabrikam support portal"],
            _ => [],
        };
    }
}

/// <summary>One row of the large dataset.</summary>
public sealed record Row(int Id, string Name, int Score);

/// <summary>A page of rows plus the total row count.</summary>
public sealed record RowPage(int Offset, Row[] Rows, int Total);

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

    /// <summary>One todo. Seeded from any cached list that contains it, so the edit page opens instantly.</summary>
    public static QueryDefinition<Todo> ById(TodoApi api, IQueryClient client, int id)
    {
        var fromList = client.GetQueryData<List<Todo>>(All.Append(TodoFilter.All));
        var cached = fromList.Data?.FirstOrDefault(t => t.Id == id);
        var definition = new QueryDefinition<Todo>(All.Append("by-id", id), ct => api.GetByIdAsync(id, ct));
        return cached is null ? definition : definition.WithInitialData(cached, fromList.DataUpdatedAt);
    }

    public static readonly QueryKey Rows = QueryKey.Create("rows");

    public static readonly InfiniteQueryOptions<int, RowPage> RowOptions = new()
    {
        InitialPageParam = 0,
        GetNextPageParam = (last, _, _) => last.Offset + last.Rows.Length < last.Total ? NextPageParam.Of(last.Offset + last.Rows.Length) : NextPageParam.None<int>(),
        QueryOptions = new QueryOptions { StaleTime = TimeSpan.FromMinutes(5) },
    };

    public static QueryDefinition<List<string>> Projects(TodoApi api, string tenant) =>
        new(QueryKey.Create("tenant", tenant, "projects"), ct => api.GetProjectsAsync(tenant, ct));
}

/// <summary>Source-generated JSON for the data synchronized across browser tabs (trim/AOT-safe).</summary>
[JsonSerializable(typeof(List<Todo>))]
internal sealed partial class TodoJson : JsonSerializerContext;
