# Migrating between releases

DotNetQuery follows [semantic versioning](releasing.md). This page lists what to change when upgrading; the
[changelog](https://github.com/msynk/dotnet-query/blob/main/CHANGELOG.md) lists everything that was added.

## Before 0.9 (builds from source) to 0.9

0.9.0 is the first version published on nuget.org. Version 0.8.0 was never published (its package IDs collided with an
unrelated library), so this section only matters if you built DotNetQuery from source or a private feed.

### Package IDs (action required)

The packages are published with the owner-scoped `Msynk.` prefix, because `DotNetQuery.Core` and
`DotNetQuery.Blazor` on nuget.org belong to an unrelated library. Replace the package references; namespaces,
assembly names and the Blazor static asset paths are unchanged, so no code changes are needed.

| Before | After |
| --- | --- |
| `DotNetQuery` | `Msynk.DotNetQuery` |
| `DotNetQuery.Blazor` | `Msynk.DotNetQuery.Blazor` |
| `DotNetQuery.Persistence.Sqlite` | `Msynk.DotNetQuery.Persistence.Sqlite` |
| `DotNetQuery.Persistence.Redis` | `Msynk.DotNetQuery.Persistence.Redis` |
| `DotNetQuery.Wpf` / `.WinUI` / `.Avalonia` / `.Maui` | `Msynk.DotNetQuery.Wpf` / `.WinUI` / `.Avalonia` / `.Maui` |
| (new) | `Msynk.DotNetQuery.Blazor.Server`, `Msynk.DotNetQuery.Uno` |

```xml
<!-- before -->
<PackageReference Include="DotNetQuery.Blazor" Version="0.8.0" />
<!-- after -->
<PackageReference Include="Msynk.DotNetQuery.Blazor" Version="0.9.0" />
```

`<script src="_content/DotNetQuery.Blazor/...">` and scoped CSS references keep working.

### API changes (compile-time)

- **`MutationOptions.RetryCount` and `InfiniteQueryOptions.RetryCount` are `int?`.** Unset (`null`) means "use the
  defaults" (`MutationDefaults.RetryCount`, else 0); `RetryCount = 0` now opts a mutation out of retries its defaults
  enable (previously 0 meant "unset" and could not be expressed). Code that only sets the property compiles unchanged;
  code that reads it needs `?? 0`.
- **`QueryStateHydration.Persist<T>`/`TryHydrate<T>` without a `JsonTypeInfo<T>` are annotated
  `[RequiresUnreferencedCode]`.** In trimmed apps use the new `JsonTypeInfo<T>` overloads, or register a context once
  with `AddDotNetQueryBlazor().UseHydrationJson(AppJson.Default)`.
- **Blazor Server reconnection** moved from a sample class into `Msynk.DotNetQuery.Blazor.Server`: replace a copied
  `QueryCircuitHandler` with `AddDotNetQueryBlazor().RefetchOnCircuitReconnect()`.

### Behavior changes (review)

These fix incorrect behavior; most apps need no change, but check code that relied on the old behavior.

- **Removing an observed query** (`RemoveQuery`, `RemoveQueries`, `Clear`) now resets it and refetches it for its
  observers. Previously the entry was dropped and its observers stopped receiving updates. Code that removed a
  query to "turn it off" while still rendering it should render something else or disable the query
  (`Enabled = false`) instead.
- **Declarative optimistic updates** apply and roll back atomically across keys, and overlapping mutations of one
  key no longer revert each other: a rolled-back mutation re-applies the other pending updates on top of the
  restored value. Two updates of the same key now roll back to the original value instead of triggering a refetch.
- **Resetting a query** detaches its in-flight fetch, so an outdated result can never land over the reset.
- **`ObservableQuery`** raises `PropertyChanged` only for properties whose values changed (previously every
  property on every change). Code that listened to `Data` to detect status changes should listen to `Status`.
- **Blazor reconnects** refetch once instead of twice.
- **Refreshes wait for pending optimistic updates.** While declarative optimistic updates of pending mutations are on a
  query, invalidating or refetching it marks it stale and the refetch runs once those mutations settled. Code that
  awaited `InvalidateQueriesAsync`/`RefetchQueryAsync` during a pending optimistic mutation and expected server data
  right after should await the mutation instead; a mutation's own `Invalidates` still refetch before it completes when no
  other optimistic update of the query is pending.
- **Prerender hydration payload.** Data carried from prerendering to the interactive render is now a JSON string
  envelope; a page prerendered by an older build and made interactive by a newer one (only possible during a rolling
  deployment) simply fetches again.
- **`PersistentMutationQueue.RestoreAsync` reconciles with the store**: entries no longer in the store (replayed or
  removed by another instance sharing it) are dropped from `Pending`. Queued mutation ids gained a per-instance
  suffix; existing ids keep working.

### New dependencies

- `Msynk.DotNetQuery` references `Microsoft.Extensions.Logging.Abstractions` (for `AddDiagnosticLogging`), alongside
  the DependencyInjection and Options abstractions it already used.
- `Msynk.DotNetQuery.Blazor` references `Microsoft.AspNetCore.Components.Authorization` (first-party).
- `Msynk.DotNetQuery.Maui` references `Microsoft.Maui.Controls` (every MAUI app already does).
- `Msynk.DotNetQuery` contains the `[GenerateQueries]` source generator. It only runs on types that carry the
  attribute; nothing is generated otherwise.

### Optional adoption

Worth a look after upgrading, none required:

- Generate keys, definitions, mutations and infinite queries from API interfaces or classes:
  [Generated queries](source-generation.md).
- Logging, `Meta` for global handlers, and the live DevTools endpoint: [Diagnostics](diagnostics.md).
- A `ReplayLock` for durable queues shared by tabs or processes: [Offline mode](offline-mode.md).
- Desktop and mobile lifecycle classes and MVVM commands, now also for Uno: [Desktop and mobile](desktop-and-mobile.md).
- Fetch timeouts, priorities, concurrency limits and memory bounds: [Performance](performance.md).
- Per-page render modes, reconnection, trim-safe hydration, forms and grids, Blazor Hybrid: [Blazor](blazor.md#render-modes).
