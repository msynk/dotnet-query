# Blazor

`DotNetQuery.Blazor` makes server state a first-class citizen in Blazor: declarative `<Query>`
rendering, hook-style `UseQuery`/`UseMutation`/`UseInfiniteQuery` for components, prerender hydration,
browser focus/online/visibility refetching, and a visual DevTools panel. It supports .NET 8 and later and
every hosting model (Server, WebAssembly, Auto, static SSR with prerendering).

## Register

```csharp
// Program.cs (server project, and the .Client project for WebAssembly/Auto)
builder.Services.AddDotNetQueryBlazor(options =>
{
    options.DefaultStaleTime = TimeSpan.FromSeconds(30);
});
```

`AddDotNetQueryBlazor` registers a **scoped** `IQueryClient`:

| Hosting model | What "scoped" means |
| --- | --- |
| WebAssembly | One scope for the whole app - one cache per browser tab. |
| Server / interactive server | One scope per user circuit - **each user has a private cache**. |
| Prerendering / static SSR | One scope per HTTP request (see hydration below). |

> Never register the client as a singleton in a Blazor Server app: a singleton cache (and its
> connectivity state) would be shared by every user of the server.

Add the signal bridge once to an interactive layout so focus, reconnect and tab-visibility trigger
refetches:

```razor
<DotNetQueryBrowserSignals />
```

## Define queries once

Describe each query as a `QueryDefinition<T>` - key, fetcher and options together - and use it
everywhere (components, prefetching, mutations, tests):

```csharp
public static class TodoQueries
{
    public static readonly QueryKey All = QueryKey.Create("todos");

    public static QueryDefinition<List<Todo>> List(TodoApi api, TodoFilter filter) =>
        new(All.Append(filter), ct => api.GetAllAsync(filter, ct),
            new QueryOptions { StaleTime = TimeSpan.FromSeconds(30) });

    public static QueryDefinition<Todo> ById(TodoApi api, int id) =>
        new(All.Append("by-id", id), ct => api.GetAsync(id, ct));
}
```

Put every input of the fetcher in the key (`filter`, `id`, page number, ...). Each combination is then
cached separately and switching back is instant.

## The `<Query>` component

```razor
<Query Definition="TodoQueries.List(Api, _filter)" KeepPreviousData="true">
    <Loading><p>Loading…</p></Loading>
    <Error Context="error"><p class="error">@error.Message</p></Error>
    <ChildContent Context="todos">
        @foreach (var todo in todos.Data!)
        {
            <TodoRow Todo="todo" />
        }
        @if (todos.IsFetching) { <small>refreshing…</small> }
        @if (todos.IsRefetchError) { <small>Showing cached data - refresh failed.</small> }
    </ChildContent>
</Query>
```

- **Templates.** `Loading` renders while the query has neither data nor an error, `Error` when the first
  load failed. `ChildContent` receives the `QueryResult<T>` for everything else (or every state, if you
  omit the other templates). The first render already shows `Loading` - there is no idle flash.
- **Key changes** (e.g. `_filter` changes) subscribe to the new key and fetch it; fresh cached data is shown
  without a request.
- **`KeepPreviousData`** keeps rendering the previous key's data (`result.IsPlaceholderData` is `true`)
  until the new key's first result arrives - flicker-free pagination, sorting and filtering.
- **`Inputs`** - when a fetcher depends on values that are *not* in the key, pass them as `Inputs`
  (structurally compared; use a tuple for several) to force a refetch when they change. Prefer putting
  inputs in the key.
- **Dependent queries** - pass `Options="new() { Enabled = userId is not null }"`; the query fetches as soon
  as it becomes enabled.
- `Key` + `Fetcher` (+ `Options`) can be used instead of `Definition`.
- `ThrowOnError = true` rethrows fetch failures from the component so the nearest `<ErrorBoundary>`
  handles them.

## Prerendering and hydration

Prerendering runs in a different DI scope (and, for WebAssembly, a different process) than the
interactive render, so the interactive client starts with an empty cache. `<Query>` solves this
automatically: data it fetched while prerendering is persisted into the page through Blazor's
`PersistentComponentState` and hydrated into the interactive client - **with its original timestamp** - before
the first interactive fetch. Within `StaleTime` the interactive render therefore neither refetches nor
flashes a loading state; older data is shown immediately and revalidated in the background.

- Set a non-zero `StaleTime` for prerendered queries; with the default zero, hydrated data is shown and
  then revalidated right away.
- Opt out per component with `PersistState="false"` (for data that must not be embedded in the HTML, or
  that is not JSON-serializable).
- In code-first components, use `QueryStateHydration.Persist<T>`/`TryHydrate<T>` with your own
  `PersistentComponentState` subscription.

On .NET 10 the same mechanism also preserves the cache when a disconnected circuit is paused and resumed.

## Hook-style components

Inherit `DotNetQueryComponentBase` to use queries and mutations from code. Each `Use*` call site owns one
subscription: later renders return the same instance, a new key replaces it, and everything is disposed
with the component.

```razor
@inherits DotNetQueryComponentBase
@inject TodoApi Api

@if (_todo.CurrentState.IsSuccess)
{
    <h2>@_todo.CurrentState.Data!.Title</h2>
    <button disabled="@_rename.CurrentState.IsPending" @onclick="RenameAsync">Rename</button>
}

@code {
    [Parameter] public int Id { get; set; }
    private QueryObserver<Todo> _todo = default!;
    private Mutation<Todo, Todo> _rename = default!;

    protected override void OnParametersSet()
    {
        _todo = UseQuery(TodoQueries.ById(Api, Id));   // re-keys automatically when Id changes
        _rename = UseMutation<Todo, Todo>(
            (todo, ct) => Api.SaveAsync(todo, ct),
            new() { Invalidates = [TodoQueries.All] });
    }

    private Task RenameAsync() => _rename.MutateAsync(_todo.CurrentState.Data! with { Title = "Renamed" });
}
```

`UseInfiniteQuery` works the same way for paginated lists. Errors from `ThrowOnError` queries are
dispatched to the nearest `ErrorBoundary`.

## Mutations with optimistic updates

```csharp
_add = QueryClient.CreateMutation<string, Todo>(
    (title, ct) => Api.AddAsync(title, ct),
    new MutationOptions<string, Todo>
    {
        OptimisticUpdates =
        [
            OptimisticUpdate.Create<string, List<Todo>>(
                TodoQueries.List(Api, TodoFilter.All).Key,
                (todos, title) => [.. todos ?? [], new Todo(-1, title, false)]),
        ],
        Invalidates = [TodoQueries.All],   // reconcile with the server once the mutation settles
    });
```

See [Optimistic Updates](optimistic-updates.md).

## DevTools

```razor
@* In MainLayout.razor, development only *@
<QueryDevToolsPanel Floating="true" />
```

A floating button opens an overlay with the cache (status, observers, staleness, timings, a JSON preview
of the cached data), the mutation history (variables, duration, errors), the event log, and actions to
refetch, invalidate, cancel or remove a query. Use `Floating="false"` (the default) to render it inline.
The component library's styles are bundled into your app's `<YourApp>.styles.css`, so make sure your
`App.razor` links it.

## Persisting the cache in the browser

`DotNetQuery.Persistence.BlazorWasm` stores registered queries in `localStorage` or IndexedDB so data
survives reloads. Register queries with source-generated JSON metadata to stay trim/AOT-safe:

```csharp
var manager = new QueryPersistenceManager(client, new IndexedDbQueryPersister(JS),
        new QueryPersistenceOptions { MaxAge = TimeSpan.FromDays(1), Buster = "v2" })
    .Register(TodoQueries.List(api, TodoFilter.All), AppJson.Default.ListTodo);

await manager.RestoreAllAsync();   // after the first render (JS interop is required)
manager.StartAutoPersist();        // save whenever the data changes
```

## Testing components

Use bUnit with `Services.AddDotNetQueryBlazor(...)`, `DefaultRetryCount = 0`, and
`this.AddFakePersistentComponentState()` to exercise hydration. Assert with `cut.WaitForAssertion(...)`,
since fetches complete asynchronously.
