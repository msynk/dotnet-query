using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetQuery;
using DotNetQuery.Synchronization;

namespace TodoApp.Client;

/// <summary>A todo as the client sees it (the server maps its own model to this).</summary>
public sealed record TodoItem(int Id, string Title, bool Done);

/// <summary>
/// How client components read and write todos. The browser (WebAssembly) uses <see cref="HttpTodoClient"/>; the server
/// registers an implementation that calls the data layer directly, for prerendering.
/// </summary>
public interface ITodoClient
{
    Task<List<TodoItem>> GetAllAsync(CancellationToken cancellationToken);

    Task<TodoItem> AddAsync(string title, CancellationToken cancellationToken);
}

/// <summary>Calls the server's minimal API.</summary>
public sealed class HttpTodoClient(HttpClient http) : ITodoClient
{
    public async Task<List<TodoItem>> GetAllAsync(CancellationToken cancellationToken) =>
        await http.GetFromJsonAsync("api/todos", TodoClientJson.Default.ListTodoItem, cancellationToken) ?? [];

    public async Task<TodoItem> AddAsync(string title, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("api/todos", title, TodoClientJson.Default.String, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(TodoClientJson.Default.TodoItem, cancellationToken))!;
    }
}

/// <summary>Query definitions shared by the WebAssembly and Auto pages.</summary>
public static class ClientTodoQueries
{
    public static readonly QueryKey All = QueryKey.Create("client-todos");

    public static QueryDefinition<List<TodoItem>> List(ITodoClient client) =>
        new(All, client.GetAllAsync, new QueryOptions { StaleTime = TimeSpan.FromSeconds(30) });
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<TodoItem>))]
[JsonSerializable(typeof(TodoItem))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(QueryEventEnvelope<TodoItem>))]
public sealed partial class TodoClientJson : JsonSerializerContext;

/// <summary>The real-time contract between the server and the WebAssembly pages.</summary>
public static class TodoEvents
{
    /// <summary>The SignalR hub path.</summary>
    public const string HubPath = "/hubs/todos";

    /// <summary>Raised with a <see cref="QueryEventEnvelope{TPayload}"/> of the new <see cref="TodoItem"/>.</summary>
    public const string Added = "TodoAdded";
}
