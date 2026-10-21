# TodoApp

A Blazor Web App (interactive server) demonstrating the [`DotNetQuery.Blazor`](../../src/adapters/DotNetQuery.Blazor)
adapter end to end:

1. **Query definitions** - [TodoQueries](TodoApi.cs) defines each todo query once (key + fetcher + options),
   and [Todos.razor](Components/Pages/Todos.razor) renders it with the `<Query>` component.
2. **Prerender hydration** - data fetched while prerendering is persisted into the page and hydrated into
   the interactive circuit, so the list does not refetch when the circuit connects.
3. **Keep previous data** - switching the All / Open / Done filter keeps the previous list on screen
   (dimmed) while the next one loads.
4. **Optimistic mutations** - adding or toggling a todo updates every affected cached list immediately,
   rolls back if the server rejects it (add a todo containing "fail" to try it), and invalidates the todo
   lists once the mutation settles.
5. **DevTools** - the floating `QueryDevToolsPanel` shows the cache, mutations and the event log live.

[TodoApi](TodoApi.cs) stands in for a remote API with simulated latency. The `IQueryClient` is scoped, so
each user circuit gets its own private cache.

## Run it

```bash
dotnet run --project samples/TodoApp
```

Then open <http://localhost:5199>. CI runs a prerender smoke test against it: the served page must contain
the todo list and the persisted query state.
