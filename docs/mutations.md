# Mutations

Queries read server state; **mutations** change it (create, update, delete, patch, or any custom operation). A mutation runs a function, tracks its lifecycle, and fires callbacks.

## A one-shot mutation

```csharp
var result = await client.MutateAsync<CreateTodo, Todo>(
    (input, ct) => api.CreateTodoAsync(input, ct),
    new CreateTodo("Buy milk"));

if (result.IsSuccess)
{
    Console.WriteLine($"Created #{result.Data!.Id}");
}
```

`MutateAsync<TVariables, TData>` takes the mutation function, the variables, and optional `MutationOptions`. It returns a `MutationResult<TVariables, TData>` (`Data`, `Error`, `Status`, `IsSuccess`, `IsError`, `EnsureData()`).

## Reusable, observable mutations

For UI, create a mutation once and observe it:

```csharp
var addTodo = client.CreateMutation<CreateTodo, Todo>(
    (input, ct) => api.CreateTodoAsync(input, ct));

addTodo.StateChanged += state => Render(state); // Idle → Pending → Success/Error

await addTodo.MutateAsync(new CreateTodo("Buy milk"));
```

`MutationState` exposes `IsIdle`, `IsPending`, `IsSuccess`, `IsError`, plus `Data`, `Error`, `Variables`, and `FailureCount`. Call `Reset()` to return to idle.

## Lifecycle callbacks

Callbacks fire in this order:

```
OnMutate  →  (OnSuccess | OnError)  →  OnSettled
```

```csharp
new MutationOptions<CreateTodo, Todo>
{
    OnMutate  = (vars, ct) => { /* optimistic update */ return ValueTask.FromResult<object?>(null); },
    OnSuccess = (data, vars, ctx, ct) => { logger.LogInformation("created {Id}", data.Id); return ValueTask.CompletedTask; },
    OnError   = (err, vars, ctx, ct)  => { /* roll back */ return ValueTask.CompletedTask; },
    OnSettled = (data, err, vars, ctx, ct) => ValueTask.CompletedTask,
}
```

The value returned from `OnMutate` is threaded through the later callbacks as `ctx` - this is how you roll back an optimistic update. See [Optimistic Updates](optimistic-updates.md).

> **Concurrency:** a single `Mutation` instance tracks one shared observable state, so use it sequentially (one submission at a time). For independent parallel mutations, prefer the one-shot `client.MutateAsync(...)`, which spins up a fresh instance per call.

## Retries

```csharp
new MutationOptions<CreateTodo, Todo>
{
    RetryCount = 3,
    RetryDelay = RetryPolicy.ExponentialWithJitter(TimeSpan.FromSeconds(1)),
}
```

## Error handling

By default a failed mutation throws from `MutateAsync` after retries are exhausted (`ThrowOnError = true`). Set `ThrowOnError = false` to capture the error into `result.Error` instead.

## Refreshing queries after a mutation

Mutations don't touch the query cache automatically. Invalidate the affected queries in `OnSuccess` (or after awaiting):

```csharp
await client.MutateAsync(fn, vars);
await client.InvalidateQueriesAsync(QueryKey.Create("todos"));
```

## Offline

Wrap mutation invocations in an `OfflineMutationQueue` to defer and replay them when connectivity returns - see [Offline Mode](offline-mode.md).
