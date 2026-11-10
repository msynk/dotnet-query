# Tutorials

Step-by-step paths through common scenarios. Each one points to runnable code in [`demo/`](https://github.com/msynk/dotnet-query/tree/main/demo) and to the
guide that explains the details.

## 1. A Blazor Web App with a CRUD list

Demo: [`demo/TodoApp`](https://github.com/msynk/dotnet-query/tree/main/demo/TodoApp) (`/` and `/todos/{id}`).

1. Install `Msynk.DotNetQuery.Blazor` and register it: `builder.Services.AddDotNetQueryBlazor(o => o.DefaultStaleTime = TimeSpan.FromSeconds(10));`.
2. Define the queries once ([TodoApi.cs](https://github.com/msynk/dotnet-query/blob/main/demo/TodoApp/TodoApi.cs) - `TodoQueries.List`, `TodoQueries.ById`), or
   generate them from your API interface with `[GenerateQueries]` ([guide](source-generation.md)).
3. Render the list with `<Query Definition="TodoQueries.List(Api, _filter)" KeepPreviousData="true">` and its
   `Loading`/`Error` templates ([Todos.razor](https://github.com/msynk/dotnet-query/blob/main/demo/TodoApp/Components/Pages/Todos.razor)).
4. Add items with an optimistic mutation: `UseMutation(fn, new() { OptimisticUpdates = [...], Invalidates = [TodoQueries.All] })`.
5. Edit with `EditForm` bound to a model copied from the cached item once, saved through a mutation
   ([EditTodo.razor](https://github.com/msynk/dotnet-query/blob/main/demo/TodoApp/Components/Pages/EditTodo.razor)).
6. Add `<QueryDevToolsPanel Floating="true" Enabled="@Environment.IsDevelopment()" />` and watch the cache.

Guides: [Blazor](blazor.md), [Mutations](mutations.md), [Optimistic updates](optimistic-updates.md).

## 2. Blazor Server, WebAssembly and Auto in one app

Demo: [`demo/TodoApp`](https://github.com/msynk/dotnet-query/tree/main/demo/TodoApp) + [`demo/TodoApp.Client`](https://github.com/msynk/dotnet-query/tree/main/demo/TodoApp.Client) (`/wasm`,
`/auto`, `/ssr`).

1. Call `AddDotNetQueryBlazor()` in both the server and the `.Client` project.
2. Put the components that run in the browser in the `.Client` project, behind an interface (`ITodoClient`) with an
   HTTP implementation for the browser and a direct one for prerendering on the server.
3. Choose the render mode per page (`@rendermode InteractiveServer` / `InteractiveWebAssembly` / `InteractiveAuto`);
   prerendered data hydrates in every mode, so nothing is fetched twice.
4. For pages that need no interactivity, `[StreamRendering]` streams the `Loading` template and then the data.
5. Call `.RefetchOnCircuitReconnect()` (package `Msynk.DotNetQuery.Blazor.Server`) in the server project, so stale
   queries refetch when a dropped Blazor Server connection comes back.
6. In trimmed WebAssembly apps, register your JSON context for hydration: `.UseHydrationJson(AppJson.Default)`.

Guide: [Blazor - render modes](blazor.md#render-modes).

## 3. Sign-in, sign-out and tenants

Demo: [`Tenants.razor`](https://github.com/msynk/dotnet-query/blob/main/demo/TodoApp/Components/Pages/Tenants.razor).

1. Keep the server client scoped (the default of `AddDotNetQueryBlazor`), so circuits never share a cache.
2. In WebAssembly, add `.ResetOnUserChange()` so the tab's cache is cleared when the signed-in user changes (pass a
   identity selector that includes the tenant claim to also reset on tenant changes).
3. Put the tenant in every key (`["tenant", id, ...]`); when switching, render the new tenant first, then
   `RemoveQueries(QueryFilter.ForKey(["tenant", previous]))` so nothing of the previous tenant stays in memory.

Guide: [Blazor - signing users in and out](blazor.md#signing-users-in-and-out).

## 4. Infinite scrolling and large lists

Demos: [`LargeList.razor`](https://github.com/msynk/dotnet-query/blob/main/demo/TodoApp/Components/Pages/LargeList.razor) (10,000 rows),
[`demo/DotNetQuery.Demo.Chat`](https://github.com/msynk/dotnet-query/tree/main/demo/DotNetQuery.Demo.Chat).

1. Create an infinite query with `InitialPageParam` and `GetNextPageParam` (offset or cursor).
2. In Blazor, feed `<Virtualize ItemsProvider>` with `UseInfiniteQuery(...).ToItemsProvider(page => page.Items, page => page.Total)`
   and add `<ScrollRestoration>` to come back to the same position.
3. Elsewhere, bind a "load more" control to `ObservableInfiniteQuery.FetchNextPageCommand`.
4. Bound memory with `MaxPages` (sliding window) for endless feeds.

Guide: [Infinite queries](infinite-queries.md).

## 5. Offline-first

Demo: [`demo/DotNetQuery.Demo.OfflineFirst`](https://github.com/msynk/dotnet-query/tree/main/demo/DotNetQuery.Demo.OfflineFirst).

1. Persist the queries the app needs offline with `QueryPersistenceManager` (`RegisterPrefix`, `RestoreAllAsync`,
   `StartAutoPersist`).
2. Queue writes in a `PersistentMutationQueue` so they survive restarts, with an idempotency key the server
   deduplicates on; with several tabs or windows over one store, give it a `ReplayLock`.
3. Report connectivity (browser signals, `MauiConnectivityBridge`, `NetworkConnectivityMonitor`) - queries pause
   offline and the queue replays on reconnect.
4. Decide what happens to a write the server rejects with `OnReplayFailure` (retry, drop, dead-letter, pause).

Guide: [Offline mode](offline-mode.md).

## 6. Optimistic updates and conflicts

Demo: [`demo/DotNetQuery.Demo.ECommerce`](https://github.com/msynk/dotnet-query/tree/main/demo/DotNetQuery.Demo.ECommerce).

1. Describe each optimistic change declaratively (`OptimisticUpdate.Create<TVariables, TData>(key, updater)`).
2. Always pair it with `Invalidates`, so the server's truth replaces the guess when the mutation settles.
3. Overlapping mutations of one key are layered: a failing one rolls back only its own change.
4. For conflicts (`409`/`412`), classify them with `HttpRetryPolicy.IsConflict` and refetch instead of retrying.

Guide: [Optimistic updates](optimistic-updates.md).

## 7. A desktop or mobile app (WPF, WinUI, Avalonia, MAUI, Uno)

Demos: [`demo/DotNetQuery.Demo.Wpf`](https://github.com/msynk/dotnet-query/tree/main/demo/DotNetQuery.Demo.Wpf),
[`demo/DotNetQuery.Demo.Avalonia`](https://github.com/msynk/dotnet-query/tree/main/demo/DotNetQuery.Demo.Avalonia),
[`demo/DotNetQuery.Demo.Maui`](https://github.com/msynk/dotnet-query/tree/main/demo/DotNetQuery.Demo.Maui).

1. Register with `services.AddDotNetQuery(...)`.
2. Expose `ObservableQuery` / `ObservableInfiniteQuery` / `ObservableMutation` from view models (created on the UI
   thread with the adapter's factories) and bind buttons to their commands.
3. Create the platform's lifecycle class at startup so returning to the app refetches stale data and queries pause
   offline.
4. Persist to the app's data directory (`MauiQueryPersistence.CreateFileSystemPersister()`, or SQLite).

Guide: [Desktop and mobile](desktop-and-mobile.md).

## 8. Diagnosing with DevTools

1. Blazor: `<QueryDevToolsPanel Floating="true" />` - queries, mutations, events, timelines, profiles (slowest
   queries), the key tree, snapshots you can restore (time travel), and the offline queue (`Queue="..."`).
2. Any app: `new QueryDevTools(client)` - the same model in code, plus `ExportJson()` for a JSON report the
   `DotNetQuery.DevTools.Cli` renders; `QueryDevToolsServer.Start(devtools)` serves it live for the CLI's `watch` mode.
3. Logs: `AddDotNetQuery().AddDiagnosticLogging()`; a `Meta` entry per query or mutation drives one global error
   handler.
4. Production: `Enabled="false"` (or tied to the environment) and `ShowData="false"`; telemetry and logs respect
   `IncludeVariablesInDiagnostics` and `DiagnosticKeyFormatter`.

Guides: [Diagnostics](diagnostics.md), [Troubleshooting](troubleshooting.md).
