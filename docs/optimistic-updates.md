# Optimistic Updates

An optimistic update applies the expected result to the cache *before* the server confirms it, so the UI feels instant — then rolls back if the request fails.

## The pattern

Use the three mutation callbacks together:

- **`OnMutate`** — snapshot the current value, apply the optimistic change, and return the snapshot as context.
- **`OnError`** — restore the snapshot from context (rollback).
- **`OnSettled`** — optionally invalidate to reconcile with the server's truth.

```csharp
await client.MutateAsync<Todo, Todo>(
    (todo, ct) => api.UpdateTodoAsync(todo, ct),
    editedTodo,
    new MutationOptions<Todo, Todo>
    {
        OnMutate = (todo, ct) =>
        {
            var key = QueryKey.Create<Todo>(todo.Id);
            var previous = client.GetQueryData<Todo>(key).Data; // snapshot
            client.SetQueryData(key, todo);                     // optimistic
            return ValueTask.FromResult<object?>(previous);      // context for rollback
        },
        OnError = (error, todo, context, ct) =>
        {
            var key = QueryKey.Create<Todo>(todo.Id);
            client.SetQueryData(key, (Todo)context!);            // rollback
            return ValueTask.CompletedTask;
        },
        OnSettled = (data, error, todo, context, ct) =>
        {
            // Reconcile with the server, whatever the outcome.
            return new ValueTask(client.InvalidateQueriesAsync(QueryKey.Create<Todo>(todo.Id), ct));
        },
    });
```

## Optimistic updates to a list

When mutating an item within a list query, snapshot and patch the list:

```csharp
OnMutate = (todo, ct) =>
{
    var listKey = QueryKey.Create("todos");
    var previous = client.GetQueryData<List<Todo>>(listKey).Data;
    if (previous is not null)
    {
        var next = previous.Select(t => t.Id == todo.Id ? todo : t).ToList();
        client.SetQueryData(listKey, next);
    }
    return ValueTask.FromResult<object?>(previous);
}
```

## Why context, not a captured local?

Threading the previous value through the callback `context` (rather than a captured variable) keeps each mutation invocation self-contained and correct even when the same mutation runs concurrently with different variables.

## Tips

- Keep optimistic state minimal — snapshot only what you change.
- Always pair an optimistic `OnMutate` with a rollback `OnError`.
- Prefer an `OnSettled` invalidation so the client eventually converges on server truth even if your optimistic guess was slightly off.
