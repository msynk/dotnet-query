# Migrating between releases

DotNetQuery follows [semantic versioning](releasing.md). This page lists what to change when upgrading; the
[changelog](https://github.com/msynk/dotnet-query/blob/main/CHANGELOG.md) lists everything that was added.

## 0.8 to the next release

### Package IDs (action required)

The packages are now published with the owner-scoped `Msynk.` prefix, because `DotNetQuery.Core` and
`DotNetQuery.Blazor` on nuget.org belong to an unrelated library. Replace the package references; namespaces,
assembly names and the Blazor static asset paths are unchanged, so no code changes are needed.

| Before | After |
| --- | --- |
| `DotNetQuery` | `Msynk.DotNetQuery` |
| `DotNetQuery.Blazor` | `Msynk.DotNetQuery.Blazor` |
| `DotNetQuery.Persistence.Sqlite` | `Msynk.DotNetQuery.Persistence.Sqlite` |
| `DotNetQuery.Persistence.Redis` | `Msynk.DotNetQuery.Persistence.Redis` |
| `DotNetQuery.Wpf` / `.WinUI` / `.Avalonia` / `.Maui` | `Msynk.DotNetQuery.Wpf` / `.WinUI` / `.Avalonia` / `.Maui` |

```xml
<!-- before -->
<PackageReference Include="DotNetQuery.Blazor" Version="0.8.0" />
<!-- after -->
<PackageReference Include="Msynk.DotNetQuery.Blazor" Version="0.9.0" />
```

`<script src="_content/DotNetQuery.Blazor/...">` and scoped CSS references keep working.

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

### New dependencies

- `Msynk.DotNetQuery.Blazor` references `Microsoft.AspNetCore.Components.Authorization` (first-party).
- `Msynk.DotNetQuery.Maui` references `Microsoft.Maui.Controls` (every MAUI app already does).
- `Msynk.DotNetQuery` contains the `[GenerateQueries]` source generator. It only runs on interfaces that carry the
  attribute; nothing is generated otherwise.

### Optional adoption

Worth a look after upgrading, none required:

- Generate keys, definitions and mutations from API interfaces: [Generated queries](source-generation.md).
- Desktop and mobile lifecycle classes and MVVM commands: [Desktop and mobile](desktop-and-mobile.md).
- Fetch timeouts, priorities, concurrency limits and memory bounds: [Performance](performance.md).
- Per-page render modes, reconnection, forms and grids in Blazor: [Blazor](blazor.md#render-modes).
