# DotNetQuery.Persistence.BlazorWasm

Browser `localStorage` cache persistence for DotNetQuery in **Blazor WebAssembly**, so cached data
survives page reloads. Implemented as an [`IQueryPersister`](../DotNetQuery.Persistence/IQueryPersister.cs)
over `IJSRuntime`. It also works on Blazor Server (JS interop is remoted, with the usual latency).

```csharp
@inject IJSRuntime JS
@inject IQueryClient Client

// e.g. in a root component's OnAfterRenderAsync (first render) / IAsyncDisposable
var persister = new LocalStorageQueryPersister(JS);
var manager = new QueryPersistenceManager(Client, persister)
    .Register<List<Todo>>(QueryKey.Create("todos"));

await manager.RestoreAllAsync();   // after first render
await manager.PersistAllAsync();   // before unload / periodically
```

Register it in DI if you prefer:

```csharp
builder.Services.AddScoped(sp =>
    new LocalStorageQueryPersister(sp.GetRequiredService<IJSRuntime>()));
```

## Notes

- All persisted queries are stored together under one `localStorage` key (default `dotnetquery:queries`)
  as a JSON map — set a custom key via the constructor. Reads/writes take the whole map; this suits a
  bounded set of registered queries persisted on shutdown and restored on startup.
- Call it only where JS interop is available (e.g. after first render in WASM) — not during prerender.
- Uses reflection-based `System.Text.Json`, so this package is **not** AOT/trim-safe.

## IndexedDB

For large datasets beyond `localStorage`'s ~5 MB quota, an IndexedDB provider is a planned follow-up — it
needs a bundled JS module for the async cursor API, so it is not included here yet.
