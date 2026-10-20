# Optimistic Updates

An optimistic update applies the expected result to the cache *before* the server confirms it, so the UI
feels instant - then rolls back if the request fails, and reconciles with the server either way.

## Declarative (recommended)

```csharp
var addTodo = client.CreateMutation<string, Todo>(
    (title, ct) => api.AddAsync(title, ct),
    new MutationOptions<string, Todo>
    {
        OptimisticUpdates =
        [
            OptimisticUpdate.Create<string, List<Todo>>(
                TodoQueries.All,                                       // the exact query key to write
                (todos, title) => [.. todos ?? [], new Todo(-1, title, Done: false)]),
        ],
        Invalidates = [TodoQueries.All],                               // reconcile once settled
    });
```

For each `OptimisticUpdate`, before the mutation function runs, DotNetQuery:

1. cancels any in-flight fetch for the key, so a refetch that started earlier cannot overwrite the
   optimistic value when it lands;
2. snapshots the entry's full state (data, timestamp, staleness);
3. writes `update(current, variables)` atomically.

If the mutation fails or is cancelled before the server accepts it, every snapshot is restored in reverse
order - exactly, including timestamps and pending invalidations. If something else wrote to the same query
in the meantime (another mutation's optimistic update, a refetch), restoring would clobber that write, so
the query is invalidated and refetched instead.

With a `QueryDefinition`, use the extension: `TodoQueries.List(api).Optimistic<List<Todo>, string>(...)`.

Updating several lists (for example "all" and "open" todos) is just more entries in `OptimisticUpdates`.

## Manual (full control)

When the optimistic change can't be expressed per key, use the callbacks:

```csharp
await client.MutateAsync<Todo, Todo>(
    (todo, ct) => api.UpdateTodoAsync(todo, ct),
    editedTodo,
    new MutationOptions<Todo, Todo>
    {
        OnMutate = async (todo, ct) =>
        {
            var key = QueryKey.Create<Todo>(todo.Id);
            await client.CancelQueriesAsync(key, ct);              // don't let a refetch overwrite us
            var previous = client.GetQueryData<Todo>(key).Data;    // snapshot
            client.SetQueryData(key, todo);                        // optimistic
            return previous;                                       // context for rollback
        },
        OnError = (error, todo, context, ct) =>
        {
            client.SetQueryData(QueryKey.Create<Todo>(todo.Id), (Todo)context!);  // rollback
            return ValueTask.CompletedTask;
        },
        Invalidates = [QueryKey.Create<Todo>()],                    // reconcile with the server
    });
```

## Infinite lists

Infinite queries store their pages in the cache as `InfiniteData<TParam, TPage>`, so they can be updated
optimistically too:

```csharp
OptimisticUpdate.Create<Message, InfiniteData<string, MessagePage>>(
    ChatQueries.Room(roomId),
    (data, message) => data! with { Pages = [data.Pages[0] with { Items = [message, .. data.Pages[0].Items] }, .. data.Pages.Skip(1)] })
```

## Tips

- Keep optimistic state minimal - write only what changes, and return new instances (never mutate the
  cached value).
- Always pair optimistic updates with `Invalidates` so the client converges on the server's truth even if
  the optimistic guess was slightly off.
- Mark placeholder rows (for example a negative id) so the UI can show "saving…" and disable actions on
  them until the real data arrives.
