# DotNetQuery.Blazor.Server

Server-only companion to `Msynk.DotNetQuery.Blazor` for Blazor Server and interactive server rendering. When the
browser's SignalR connection to a circuit drops and comes back, the browser's own online/offline events do not fire
and the queries on screen may have gone stale meanwhile; `QueryCircuitHandler` refetches them.

```csharp
// The server project's Program.cs
builder.Services.AddDotNetQueryBlazor().RefetchOnCircuitReconnect();
```

`QueryCircuitHandlerOptions` chooses which queries to refresh (default: the rendered, stale ones) and whether to
invalidate them (refreshing fresh data too) instead of refetching only stale ones. The package references the ASP.NET
Core shared framework, which Blazor WebAssembly cannot load - that is why it is separate from the WebAssembly-compatible
adapter. See the [Blazor guide](../../../docs/blazor.md#blazor-server-reconnection).
