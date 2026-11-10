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
  omit the other templates). The first render already shows `Loading` - there is no idle flash. `Empty`
  renders instead of `ChildContent` when the query succeeded with `null` or an empty collection (override
  what counts as empty with `IsEmpty="@(n => n == 0)"`).
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
- In code-first components, use `QueryStateHydration.Persist`/`TryHydrate` with your own
  `PersistentComponentState` subscription (the `JsonTypeInfo<T>` overloads are trim-safe).

### Trimmed and NativeAOT WebAssembly apps

The data is serialized as JSON. Register your source-generated `JsonSerializerContext` once, in both the server and the
`.Client` project, so hydration needs no reflection:

```csharp
builder.Services.AddDotNetQueryBlazor().UseHydrationJson(AppJson.Default);
```

Types the context does not cover fall back to reflection-based JSON, which works with Blazor's default (partial)
trimming because your own assemblies are not trimmed. With `TrimMode=full`, or with reflection-based serialization
disabled, register every type you prerender; a payload that cannot be read is skipped and the interactive render
fetches it instead. The adapter itself is trim- and AOT-annotated, and CI publishes a trimmed WebAssembly app to keep
it warning-free.

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

`UseQuery(definition, select, notifyOn)` subscribes through a selector, so the component re-renders only when
the selected value changes - for example a badge that counts open todos does not re-render on background
refetches or on edits to done items:

```csharp
_openCount = UseQuery(TodoQueries.List(Api), todos => todos.Count(t => !t.Done), QueryStateProperties.Data);
```

Hook-style queries prerender like `<Query>`: while prerendering, the component waits for the fetches its
`Use*` calls started (so the HTML contains the data), persists the results into the page, and the interactive
render hydrates them with their original timestamps instead of fetching again. Override
`PersistQueryState => false` for data that must not be embedded in the page.

### Dynamic lists of queries

`UseQueries(definitions)` subscribes to a list that changes between renders (TanStack's `useQueries`): each query is
fetched in parallel; queries that leave the list are unsubscribed and new ones added, and the returned observers
match the list's order.

```csharp
_users = UseQueries(SelectedIds.Select(id => UserQueries.ById(Api, id)).ToList());
```

### Rendering only what matters

`<Query NotifyOn="QueryStateProperties.Data | QueryStateProperties.Error">` re-renders only for those changes (no
re-render for background-refetch status flips); `UseQuery(definition, select, notifyOn)` does the same for hooks.

### Several clients

`<QueryClientProvider Client="@client">` makes the components below it - `<Query>`, `DotNetQueryComponentBase`, the
DevTools panel - use that client instead of the registered one (a separate cache for public reference data, an
isolated cache in a preview or a test).

### Navigation

`<DotNetQueryNavigation />` (once, in an interactive layout) cancels the fetches of queries nothing renders any more
when the user navigates away, and with `RefetchStaleOnNavigation="true"` refetches the stale queries the new page
shows.

### Long lists: Virtualize and scroll restoration

```razor
<Virtualize ItemsProvider="_rows" Context="row"><RowView Row="row" /></Virtualize>
<ScrollRestoration Id="rows" Ready="@_rowsLoaded" />

@code {
    private ItemsProviderDelegate<Row> _rows = default!;
    protected override void OnInitialized() =>
        _rows = UseInfiniteQuery(RowQueries.Key, Api.GetRowsAsync, RowQueries.Options).ToItemsProvider(page => page.Items, page => page.Total);
}
```

`ToItemsProvider` loads the pages covering the rows Virtualize asks for (through the cache, so scrolling back is
instant); without a total it keeps one page of headroom so scrolling continues. `<ScrollRestoration>` remembers the
scroll position per id (in `sessionStorage`) and restores it when the view renders again.

## Render modes

The adapter works the same in every interactive render mode, and a Blazor Web App can mix them per page (the
[TodoApp demo](https://github.com/msynk/dotnet-query/tree/main/demo/TodoApp) does: `@rendermode InteractiveServer`, `InteractiveWebAssembly` and
`InteractiveAuto` pages side by side, all covered by the Playwright tests).

- **Register in both projects.** Call `AddDotNetQueryBlazor()` in the server's `Program.cs` and in the `.Client`
  project's. Prerendering of a WebAssembly or Auto page runs on the server with the server's registrations; the
  interactive render runs in the browser with the client's.
- **Fetch through an abstraction.** A component that runs in both places should not call a server-only service
  directly. Put an interface in the `.Client` project (`ITodoClient`), implement it over `HttpClient` in the browser
  and over the data layer on the server, and build query definitions on the interface. Keys stay identical, so the
  prerendered state hydrates into the browser cache and no request is repeated after the switch.
- **Persisted state is per render mode.** Blazor writes separate prerender state for server and WebAssembly
  components (`Blazor-Server-Component-State` / `Blazor-WebAssembly-Component-State` in the page); the adapter's
  hydration uses whichever applies, so nothing needs configuring.
- **Disable inputs until interactive.** `RendererInfo.IsInteractive` is `false` while prerendering; disabling submit
  buttons until then avoids actions that would be lost before the page becomes interactive.

### Blazor Server reconnection

Browser online/offline events do not cover a dropped SignalR connection to the circuit. The server-only
`Msynk.DotNetQuery.Blazor.Server` package (it references the ASP.NET Core shared framework, which WebAssembly cannot
load, so it is separate from the adapter) registers a `CircuitHandler` that refetches the stale queries the user is
looking at when the connection comes back:

```csharp
// The server project's Program.cs
builder.Services.AddDotNetQueryBlazor().RefetchOnCircuitReconnect();

// Or refresh more: invalidate every rendered query, fresh ones included (the server may have changed meanwhile)
builder.Services.AddDotNetQueryBlazor().RefetchOnCircuitReconnect(new QueryCircuitHandlerOptions
{
    Filter = new QueryFilter { Type = QueryTypeFilter.Active },
    Invalidate = true,
});
```

### Forms and grids

- **`EditForm`**: load the entity with `UseQuery`, copy it into a form model once (not on every render, or the
  user's edits are overwritten by background refetches), and save through `UseMutation` with an optimistic update
  of the detail and list keys plus `Invalidates`. See
  [EditTodo.razor](https://github.com/msynk/dotnet-query/blob/main/demo/TodoApp/Components/Pages/EditTodo.razor).
- **`QuickGrid`**: make each page a query, so paging back and forth is served from the cache and pages are
  deduplicated and refreshed like any query:

```razor
<QuickGrid ItemsProvider="_provider" Pagination="_pagination">...</QuickGrid>
<Paginator State="_pagination" />

@code {
    private readonly PaginationState _pagination = new() { ItemsPerPage = 10 };
    private GridItemsProvider<Row> _provider = default!;

    protected override void OnInitialized() => _provider = async request =>
    {
        var (offset, count) = (request.StartIndex, request.Count ?? 10);
        var page = await Client.FetchAsync(
            QueryKey.Create("grid", offset, count),
            ct => Api.GetRowsAsync(offset, count, ct),
            new QueryOptions { StaleTime = TimeSpan.FromMinutes(1) },
            cancellationToken: request.CancellationToken);
        var data = page.EnsureData();
        return GridItemsProviderResult.From(data.Rows, data.Total);
    };
}
```

### Real-time updates (SignalR)

Server pushes map onto the cache through a `QueryEventSynchronizer` (`Msynk.DotNetQuery.SignalR`): typed mappings to
invalidations and incremental updates, with duplicate and ordering protection, group resubscription and recovery after
every reconnect - see [Real-time synchronization](realtime-sync.md). Components can own mappings for as long as they are
shown and render the connection status:

```csharp
builder.Services.AddDotNetQueryBlazor()
    .AddSignalRSync(_ => new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect().Build(),
        sync => sync.Map<QueryEventEnvelope<Todo>>("TodoChanged", m => m.Upsert(TodoQueries.Entity, e => e.Payload)),
        new HubConnectionQueryEventSourceOptions { OwnsConnection = true })
    .AddCrossTabEventRelay();   // optional: tabs share event notices; place <DotNetQueryCrossTabEvents /> in the layout
```

```razor
@inherits DotNetQueryComponentBase
@inject QueryEventSynchronizer Sync

<span class="live">@UseQuerySyncStatus(Sync)</span>

@code {
    [Parameter] public int RoomId { get; set; }

    protected override void OnParametersSet() =>
        UseQueryEvents<Message>(Sync, "MessageAdded", m => m
            .Where(msg => msg.RoomId == RoomId)
            .Update<List<Message>>(ChatKeys.Room(RoomId), (list, msg) => [.. list, msg]),
            dependsOn: RoomId);   // removed with the component, re-registered when the room changes
}
```

In WebAssembly, start the synchronizer from a component after the first render (see the TodoApp demo's
`LiveUpdates.razor`) - the renderer's scope holds the app's query client.

For high-frequency streams, `StreamIntoCacheAsync` (see [Advanced scenarios](advanced-scenarios.md)) folds an
`IAsyncEnumerable` into one cached value.

## Initial and placeholder data

Definitions can carry data to show before the first fetch completes:

```csharp
// Initial data is real cached data (seeded once, persisted, restored by ResetQueriesAsync). Fresh initial
// data is served without a request; stale initial data is shown and refetched in the background.
var detail = TodoQueries.ById(Api, id) with
{
    InitialData = () => Client.GetQueryData(TodoQueries.List(Api)).Data?.FirstOrDefault(t => t.Id == id)!,
    InitialDataUpdatedAt = Client.GetQueryData(TodoQueries.List(Api)).DataUpdatedAt,
};

// Placeholder data is never cached: shown while pending, flagged with IsPlaceholderData.
var skeleton = TodoQueries.List(Api).WithPlaceholderData([new Todo(0, "Loading…", false)]);
```

`<Query Definition="...">`, `UseQuery(definition)`, `client.Watch(definition)` and `ObservableQuery` all honor
both. A placeholder result is not pending, so `<Query>` renders `ChildContent` (not `Loading`) with
`result.IsPlaceholderData` set; `KeepPreviousData` takes precedence over the placeholder when both apply.

## Signing users in and out

A scoped client is private to one circuit (Blazor Server) or one browser tab (WebAssembly) - but in
WebAssembly that tab outlives a sign-out, so the next user to sign in would see the previous user's cached
data. Opt in to clearing the cache whenever the signed-in identity changes:

```csharp
builder.Services.AddDotNetQueryBlazor().ResetOnUserChange();

// Also reset when the active tenant changes:
builder.Services.AddDotNetQueryBlazor().ResetOnUserChange(user =>
    $"{QueryAuthenticationGuard.DefaultIdentity(user)}|{user.FindFirst("tenant")?.Value}");
```

The `QueryAuthenticationGuard` listens to `AuthenticationStateProvider` (activated by the first `<Query>` or
`DotNetQueryComponentBase` component). The first identity is recorded without a reset; on every later change
queries nobody renders are removed, rendered queries are reset and refetched under the new identity (no stale
data is shown even briefly), and settled mutation runs are forgotten. Token refreshes for the same user do not
reset anything. Persisted caches and offline mutation queues live outside the client - clear them in
`guard.UserChanged`, or call `guard.Reset()` from an explicit sign-out handler.

## Keeping browser tabs in sync

Register the queries to share between tabs and place the starter component in an interactive layout:

```csharp
builder.Services.AddDotNetQueryBlazor()
    .AddCrossTabSync(sync => sync.RegisterPrefix(TodoQueries.All, TodoJson.Default.ListTodo));
```

```razor
<DotNetQueryCrossTabSync />
```

A todo added in one tab - including its optimistic placeholder and, if the server rejects it, the rollback -
appears in every other tab of the same origin without a refetch, and invalidations reach every tab. It runs
over the browser's `BroadcastChannel` (`BroadcastChannelQuerySync`) and the core `QuerySynchronizer` (see
[Advanced Scenarios](advanced-scenarios.md#synchronizing-caches-tabs-windows-processes)). Use the
`JsonTypeInfo<T>` registrations in trimmed WebAssembly apps. In Blazor Server each tab's circuit keeps its own
cache on the server and the browser relays the data between them.

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
of the cached data), the mutation history (variables, duration, errors, and whether a run is paused waiting
for its scope or connectivity), the event log, and actions to refetch, invalidate, cancel or remove a query.
Use `Floating="false"` (the default) to render it inline.

Keep it away from production users and their data:

```razor
@inject IWebHostEnvironment Environment   @* IWebAssemblyHostEnvironment in WebAssembly *@
<QueryDevToolsPanel Floating="true" Enabled="@Environment.IsDevelopment()" ShowData="false" />
```

A disabled panel renders nothing and does not observe the client; `ShowData="false"` hides cached values and
mutation variables while keeping keys, statuses and timings.

The panel also offers a key-hierarchy tree, a status filter, a per-query timeline and profile with a Reset action,
cache snapshots with one-click restore (time travel), and - with `Queue="@offlineQueue"` - an "Offline queue" tab
to inspect pending and dead-lettered mutations, retry or discard them. See [Diagnostics](diagnostics.md).
The component library's styles are bundled into your app's `<YourApp>.styles.css`, so make sure your
`App.razor` links it.

## Persisting the cache in the browser

`LocalStorageQueryPersister` and `IndexedDbQueryPersister` (namespace `DotNetQuery.Persistence`) store
registered queries in `localStorage` or IndexedDB so data survives reloads. For a durable mutation queue stored in
IndexedDB, add `WebLocksMutationQueueLock` so only one tab replays each queued write (see
[Offline mode](offline-mode.md#several-tabs-or-processes-sharing-the-queue)). Register queries with
source-generated JSON metadata to stay trim/AOT-safe:

```csharp
var manager = new QueryPersistenceManager(client, new IndexedDbQueryPersister(JS),
        new QueryPersistenceOptions { MaxAge = TimeSpan.FromDays(1), Buster = "v2" })
    .Register(TodoQueries.List(api, TodoFilter.All), AppJson.Default.ListTodo);

await manager.RestoreAllAsync();   // after the first render (JS interop is required)
manager.StartAutoPersist();        // save whenever the data changes
```

## Blazor Hybrid (MAUI, WPF, WinForms)

In a `BlazorWebView` the components run in the app's process, so the adapter works unchanged - with a few choices to
make:

- **Registration** - `AddDotNetQueryBlazor()` registers a scoped client, and each `BlazorWebView` has its own scope:
  one cache per web view. When native pages (XAML view models) and the web view must share one cache, register a
  singleton client first with `services.AddDotNetQuery()` and then `AddDotNetQueryBlazor()` (registrations that already
  exist are kept, so configure the options in the first call), and components and view models resolve the same
  `IQueryClient`.
- **Signals** - `<DotNetQueryBrowserSignals />` reports the web view's focus, visibility and `online`/`offline` events;
  the platform lifecycle class (`MauiQueryLifecycle`, `WpfQueryLifecycle`) reports app resume and the operating
  system's connectivity. Use both when native and web content share the cache.
- **Persistence** - there is no prerendering, so hydration does not apply. Persist to the app's data directory
  (`MauiQueryPersistence.CreateFileSystemPersister()`, SQLite) rather than `localStorage`/IndexedDB, which some web
  views clear with the app's cache.
- **Sign-out** - a single-user device app usually does not need `ResetOnUserChange()`; call `client.Clear()` (and
  `ClearAsync` on a durable queue) from the sign-out handler instead.

## Testing components

Use bUnit with `Services.AddDotNetQueryBlazor(...)`, `DefaultRetryCount = 0`, and
`this.AddFakePersistentComponentState()` to exercise hydration. Assert with `cut.WaitForAssertion(...)`,
since fetches complete asynchronously.
