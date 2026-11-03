# TodoApp

A Blazor Web App demonstrating the [`DotNetQuery.Blazor`](../../src/adapters/DotNetQuery.Blazor) adapter end to
end, in every interactive render mode. Each page picks its own render mode (there is no global one), so the same
library is exercised on the server, in the browser (WebAssembly) and in Auto mode side by side.

| Page | Render mode | What it shows |
| --- | --- | --- |
| [Todos](Components/Pages/Todos.razor) (`/`) | Interactive Server | Query definitions, prerender hydration, keep-previous-data, optimistic mutations with rollback, cross-tab sync, DevTools |
| [Edit](Components/Pages/EditTodo.razor) (`/todos/{id}`) | Interactive Server | `EditForm` bound to a cached query, saved through an optimistic mutation that updates the detail and the list at once and invalidates the lists when it settles |
| [Large list](Components/Pages/LargeList.razor) (`/large`) | Interactive Server | 10,000 rows through an infinite query and `<Virtualize ItemsProvider>`, with `<ScrollRestoration>` |
| [Grid](Components/Pages/Grid.razor) (`/grid`) | Interactive Server | `QuickGrid` paging through an `ItemsProvider` where each page is a cached, deduplicated query |
| [Tenants](Components/Pages/Tenants.razor) (`/tenants`) | Interactive Server | Tenant-scoped keys; switching tenants removes the previous tenant's cached data |
| [WebAssembly](../TodoApp.Client/Pages/WasmPage.razor) (`/wasm`) | Interactive WebAssembly | A browser-side cache hydrated from the server prerender (no API call on load), mutations over HTTP |
| [Auto](../TodoApp.Client/Pages/AutoPage.razor) (`/auto`) | Interactive Auto | The same component starting on the server and moving to WebAssembly once it is downloaded |

Highlights:

1. **Query definitions** - [TodoQueries](TodoApi.cs) defines each query once (key + fetcher + options), and the
   pages render them with the `<Query>` component.
2. **Prerender hydration** - data fetched while prerendering is persisted into the page and hydrated into the
   interactive circuit (or the WebAssembly runtime), so nothing refetches when the page becomes interactive.
3. **Keep previous data** - switching the All / Open / Done filter keeps the previous list on screen (dimmed)
   while the next one loads.
4. **Optimistic mutations** - adding or toggling a todo updates every affected cached list immediately, rolls
   back if the server rejects it (add a todo containing "fail" to try it), and invalidates the lists once the
   mutation settles.
5. **Circuit reconnection** - [QueryCircuitHandler](QueryCircuitHandler.cs) refetches stale active queries when a
   dropped circuit reconnects.
6. **DevTools** - the floating `QueryDevToolsPanel` shows the cache, mutations and the event log live.

[TodoApi](TodoApi.cs) stands in for a remote API with simulated latency; the WebAssembly pages reach it through
the minimal API in [Program.cs](Program.cs) (`/api/todos`). The server's `IQueryClient` is scoped, so each user
circuit gets its own private cache; in the browser each tab has its own.

[TodoApp.Client](../TodoApp.Client) holds the components that run in WebAssembly and the `ITodoClient`
abstraction they use: [HttpTodoClient](../TodoApp.Client/TodoClient.cs) in the browser and
[ServerTodoClient](ServerTodoClient.cs) while prerendering.

## Run it

```bash
dotnet run --project samples/TodoApp
```

Then open <http://localhost:5199>. CI runs a smoke test against it (the served pages must contain the todo list
and the persisted query state, and the API must answer), and the Playwright tests in
[tests/DotNetQuery.E2E](../../tests/DotNetQuery.E2E) drive every page in a real browser.

Open the app in two tabs and add a todo in one: it appears in the other immediately, through cross-tab
synchronization (`AddCrossTabSync` in `Program.cs`, `<DotNetQueryCrossTabSync />` in
[QueryInfrastructure](Components/QueryInfrastructure.razor)).
