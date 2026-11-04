---
_layout: landing
---

# DotNetQuery

Server-state management for .NET - fetching, caching, synchronizing and mutating remote data - with Blazor as the
flagship integration and adapters for WPF, WinUI, Avalonia, MAUI and Uno. Inspired by TanStack Query, built for .NET:
trim- and NativeAOT-safe, with no UI dependencies in the core.

```csharp
builder.Services.AddDotNetQueryBlazor(o => o.DefaultStaleTime = TimeSpan.FromSeconds(30));
```

```razor
<Query Definition="TodoQueries.List(Api)">
    <Loading><p>Loading…</p></Loading>
    <ChildContent Context="todos">
        @foreach (var todo in todos.Data!) { <li>@todo.Title</li> }
    </ChildContent>
</Query>
```

## Start here

- [Getting started](getting-started.md) - install, register, first query, query definitions.
- [Tutorials](tutorials.md) - CRUD, render modes, auth and tenants, infinite scrolling, offline-first, desktop and
  mobile, DevTools.
- [Blazor](blazor.md) and [Desktop and mobile](desktop-and-mobile.md) - the integrations.
- [Generated queries](source-generation.md) - keys, definitions, mutations and infinite queries from Refit,
  OpenAPI-generated or Kiota-wrapping APIs.

## Concepts

[Caching](caching.md) · [Mutations](mutations.md) · [Optimistic updates](optimistic-updates.md) ·
[Infinite queries](infinite-queries.md) · [Offline mode](offline-mode.md) · [Diagnostics](diagnostics.md) ·
[Performance](performance.md) · [Advanced scenarios](advanced-scenarios.md) ·
[Architecture and lifecycle](architecture.md)

## Help

[Troubleshooting](troubleshooting.md) · [Coming from TanStack Query](tanstack-migration.md) ·
[Migrating between releases](migration.md) · [Versioning and releases](releasing.md) ·
[API reference](xref:DotNetQuery) · [Source on GitHub](https://github.com/msynk/dotnet-query)
