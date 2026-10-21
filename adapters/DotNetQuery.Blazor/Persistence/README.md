# Browser persistence (Blazor)

Browser cache persistence for DotNetQuery in **Blazor WebAssembly**, shipped in the `DotNetQuery.Blazor`
package, so cached data survives page reloads. Two
[`IQueryPersister`](../../../src/DotNetQuery/Persistence/IQueryPersister.cs) providers over `IJSRuntime`:
`LocalStorageQueryPersister` and `IndexedDbQueryPersister`. Both also work on Blazor Server (JS interop is
remoted, with the usual latency).

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
  as a JSON map - set a custom key via the constructor. Reads/writes take the whole map; this suits a
  bounded set of registered queries persisted on shutdown and restored on startup.
- Call it only where JS interop is available (e.g. after first render in WASM) - not during prerender.
- Trim/AOT-safe: envelopes are serialized with source-generated JSON. Register queries with
  `manager.Register(key, AppJson.Default.MyType)` (a `JsonTypeInfo<T>`) to keep payloads trim-safe too.
- Corrupt or incompatible stored data is skipped rather than failing the restore.

## IndexedDB

For datasets beyond `localStorage`'s ~5 MB quota, `IndexedDbQueryPersister` stores each query as its own
record in an IndexedDB object store. It uses a small JS module shipped with the package - reference it once
from your Blazor host page:

```html
<script src="_content/DotNetQuery.Blazor/dotnetquery-indexeddb.js"></script>
```

```csharp
var persister = new IndexedDbQueryPersister(JS);            // defaults: db "dotnetquery", store "queries"
var manager = new QueryPersistenceManager(Client, persister)
    .Register<List<Todo>>(QueryKey.Create("todos"));
await manager.RestoreAllAsync();
await manager.PersistAllAsync();
```

Unlike the localStorage provider (which keeps all queries in one JSON blob), IndexedDB stores each query as
a separate keyed record, so it scales to many/large queries.
