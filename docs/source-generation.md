# Generated queries from API interfaces (Refit, OpenAPI, Kiota)

Writing a key and a fetcher for every endpoint is repetitive and easy to get subtly wrong (two keys for the same
data, an invalidation that misses a query). `[GenerateQueries]` generates them from the API you already have - a Refit
interface, a client generated from an OpenAPI document, a hand-written service interface, or a class (for example a
thin wrapper over a Kiota client). The
generator ships inside the `Msynk.DotNetQuery` package; there is nothing else to install. The generated code is
plain C# (no reflection), so it is trim- and NativeAOT-safe.

## A hand-written or Refit interface

```csharp
[GenerateQueries]
public interface ITodoApi
{
    [Get("/todos")]
    Task<List<Todo>> GetTodosAsync(TodoFilter filter = TodoFilter.All, CancellationToken cancellationToken = default);

    [Get("/todos/{id}")]
    Task<Todo> GetTodoAsync(int id, CancellationToken cancellationToken);

    [Post("/todos")]
    [MutationMethod(Invalidates = [nameof(GetTodosAsync)])]
    Task<Todo> AddTodoAsync([Body] NewTodo todo, CancellationToken cancellationToken);

    [Put("/todos/{id}")]
    Task<Todo> RenameTodoAsync(int id, [Body] string title, CancellationToken cancellationToken);

    [Delete("/todos/{id}")]
    Task DeleteTodoAsync(int id);
}
```

This generates `TodoApiQueries`:

```csharp
// Keys - hierarchical, under one prefix per API
TodoApiQueries.All                            // ["TodoApi"]
TodoApiQueries.Keys.GetTodo(5)                // ["TodoApi", "GetTodo", 5]
TodoApiQueries.Keys.GetTodoPrefix             // ["TodoApi", "GetTodo"] - every GetTodo query

// Query definitions - use them anywhere a QueryDefinition<T> is accepted
await client.FetchAsync(TodoApiQueries.GetTodo(api, 5));
using var todos = client.Watch(TodoApiQueries.GetTodos(api, TodoFilter.Open));
<Query Definition="TodoApiQueries.GetTodos(Api)">...</Query>
_todo = UseQuery(TodoApiQueries.GetTodo(Api, Id, new QueryOptions { StaleTime = TimeSpan.FromMinutes(1) }));

// Mutations - with a mutation key and invalidation already set
var add = TodoApiQueries.AddTodo(client, api);                  // invalidates every GetTodos query
await add.MutateAsync(new NewTodo("Buy milk"));

var rename = TodoApiQueries.RenameTodo(client, api);            // several parameters: a generated record
await rename.MutateAsync(new TodoApiQueries.RenameTodoVariables(5, "Buy oat milk"));

// The pieces, for UseMutation or extra options (optimistic updates, scopes, retries)
_add = UseMutation(TodoApiQueries.AddTodoFn(Api), TodoApiQueries.AddTodoOptions(new() { OptimisticUpdates = [...] }));
```

## A client generated from an OpenAPI document

Generate a client interface from the OpenAPI document with the tool you prefer, then point the attribute at it from
your own code - the generated file is never edited:

- **[Refitter](https://github.com/christianhelle/refitter)** generates a Refit interface (`IPetStoreApi`); its
  `[Get]`/`[Post]`/... attributes classify the methods.
- **NSwag** generates a client class with an interface (`IPetStoreClient`) where every operation has an overload with
  and without a `CancellationToken`; the generator keeps one member per operation (the one with the token).
- **Kiota** generates request builders rather than an interface; wrap the calls you need in a small class of your own
  and annotate the class (no interface needed):

```csharp
[GenerateQueries(KeyPrefix = "users")]
public sealed class UsersApi(GraphServiceClient graph)
{
    public Task<User?> GetUserAsync(string id, CancellationToken cancellationToken) =>
        graph.Users[id].GetAsync(cancellationToken: cancellationToken);
}

await client.FetchAsync(UsersApiQueries.GetUser(usersApi, "42"));
```

  On a class, the public instance methods (inherited ones included) are used; `Dispose`/`DisposeAsync` and members of
  `object` are skipped.

```csharp
// Anywhere in the project (for example GlobalUsings.cs or Program.cs)
[assembly: GenerateQueries(typeof(IPetStoreClient), KeyPrefix = "petstore")]

await client.FetchAsync(PetStoreClientQueries.GetPetById(petStore, 7));
var addPet = PetStoreClientQueries.AddPet(client, petStore);
```

Regenerating the client from a changed OpenAPI document regenerates the queries with it: a removed endpoint becomes
a compile error at the call sites instead of a runtime failure.

## How methods are classified

1. `[QueryMethod]` / `[MutationMethod]` decide explicitly; `[QueryIgnore]` skips a method.
2. Otherwise Refit's verb attributes: `[Get]` and `[Head]` are queries; `[Post]`, `[Put]`, `[Patch]` and `[Delete]`
   are mutations.
3. Otherwise the name: methods starting with `Get`, `List`, `Find`, `Search`, `Fetch`, `Load`, `Query`, `Count` or
   `Read` are queries; anything else is a mutation.

Queries must return `Task<T>` or `ValueTask<T>`; mutations may also return `Task` or `ValueTask` (the mutation's data
is then `true`). A `CancellationToken` parameter receives the query's or mutation's token. Every other parameter is
part of the query key, so it needs value equality (primitives, strings, enums, records). `ref`/`out`/`in` parameters
are not supported; annotated methods that cannot be generated are reported (DNQ001), unannotated ones are skipped.

### Generic methods

A generic method generates generic members with the same constraints; each type argument becomes a key segment, so
`GetItem<Todo>(api, 1)` and `GetItem<User>(api, 1)` are different queries under the same `Keys.GetItemPrefix`:

```csharp
Task<T> GetItemAsync<T>(int id, CancellationToken cancellationToken) where T : class;

var todo = await client.FetchAsync(StoreApiQueries.GetItem<Todo>(api, 1));
```

## Infinite queries

Mark a paginated method with `[InfiniteQueryMethod("<page parameter>")]`. The key leaves the page parameter out, and the
generated class gets the page function and a factory:

```csharp
[InfiniteQueryMethod("cursor")]
Task<FeedPage> GetFeedAsync(string topic, string? cursor, CancellationToken cancellationToken);
```

```csharp
FeedApiQueries.Keys.GetFeed("news")                  // ["FeedApi", "GetFeed", "news"] - the pages of one feed
using var feed = FeedApiQueries.GetFeed(client, api, new InfiniteQueryOptions<string?, FeedPage>
{
    InitialPageParam = null,
    GetNextPageParam = (last, _, _) => last.Next is { } next ? NextPageParam.Of<string?>(next) : NextPageParam.None<string?>(),
}, "news");

// Blazor hook or MVVM binding: the key and the page function
_feed = UseInfiniteQuery(FeedApiQueries.Keys.GetFeed(Topic), FeedApiQueries.GetFeedPageFn(Api, Topic), FeedOptions);
```

Infinite queries are invalidated like queries (`Invalidates = [nameof(GetFeedAsync)]`, or the whole API by default).

## Invalidation

A mutation invalidates every query of its API by default - always correct, sometimes more than needed. Narrow it with
`[MutationMethod(Invalidates = [nameof(ITodoApi.GetTodosAsync)])]` (every query of the listed methods, whatever their
arguments; unknown names are reported as DNQ002), turn it off with `Invalidates = []`, or override it per use:
`TodoApiQueries.AddTodo(client, api, new() { Invalidates = [TodoApiQueries.Keys.GetTodo(5)] })`.

## Naming

| Attribute property | Default | Effect |
| --- | --- | --- |
| `KeyPrefix` | interface name without the leading `I` | first segment of every key and mutation key |
| `ClassName` | `<Name>Queries` | the generated static class (partial, so you can add members) |
| `QueryMethod.Name` / `MutationMethod.Name` | method name without `Async` | member names and the key segment |

The class is generated in the interface's namespace, `public` when the interface is public and `internal`
otherwise.
