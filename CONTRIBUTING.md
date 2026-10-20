# Contributing to DotNetQuery

## Build & test

```bash
dotnet build DotNetQuery.slnx -c Release
dotnet test DotNetQuery.slnx -c Release
```

The full solution (including the WPF and WinUI adapters, which target Windows) builds on Windows. The
core and cross-platform adapters build and test on Linux/macOS too; CI runs both.

**Platform adapters.** `DotNetQuery.Maui` is **not** in the solution (its platform heads need workloads
and per-OS tooling). Build it directly:

```bash
dotnet build adapters/DotNetQuery.Maui -f net10.0-android      # needs the maui/android workloads
```

Its target heads are environment-gated, so each builds where it can:

- **Android** - `dotnet` CLI, VS, and CI.
- **Windows** (`net10.0-windows10.0.19041.0`) - only under full Visual Studio MSBuild (the head is gated
  on MSBuild runtime type `Full`, since the transitive WindowsAppSDK PRI/Appx tasks aren't in the bare
  SDK): `& "$(vswhere ... -find MSBuild\**\Bin\MSBuild.exe)" adapters/DotNetQuery.Maui -restore`.
- **iOS / Mac Catalyst** - added when building on macOS.

`DotNetQuery.WinUI` builds with the plain CLI because it excludes the WindowsAppSDK MSBuild targets (see
its README).

## Public API surface is frozen

The shipped libraries enable `Microsoft.CodeAnalysis.PublicApiAnalyzers`. Every public member is
recorded in a `PublicAPI.Shipped.txt` (released surface) / `PublicAPI.Unshipped.txt` (pending surface)
pair next to each project. Any public API you add, remove, or change **fails the build** (warnings are
errors) until the baseline is updated - this is intentional, so surface changes are always deliberate and
reviewable.

When you intentionally change public API, regenerate the affected project's baseline:

```bash
dotnet format analyzers <path-to-project> --diagnostics RS0016
```

This appends new members to `PublicAPI.Unshipped.txt`. Review the diff - an unexpected entry means you
exposed something you didn't intend. On a release, move `Unshipped` entries into `Shipped.txt`.

Analyzer-enabled projects: `DotNetQuery.Core`, `.DependencyInjection`, `.DevTools`, `.Persistence`,
`.Persistence.Sqlite`, `.Persistence.Redis`, `.Persistence.BlazorWasm`, `.Http`, `.GraphQL`, `.Mvvm`,
`.Wpf`, `.WinUI`, `.Avalonia`, `.Maui`. The Razor adapters (`.Blazor`, `.DevTools.Blazor`) are excluded
(their public surface is largely Razor-generated; component parameters are covered by bUnit tests instead).

## Concurrency rules for the engine

`QueryEntry<T>` is the heart of the library; changes to it must keep these invariants (each is covered by
tests in `FetchLifecycleTests`, `OfflineBehaviorTests` and `ConcurrencyRegressionTests`):

- User code (fetchers, updaters, observer and cache-change handlers) never runs while an entry lock is held.
- State changes are queued under the lock and delivered by a single drainer, so subscribers see every
  transition once and in order.
- A cancelled or superseded fetch never leaves a query in `Loading`/`Refetching`; waiters of a superseded
  fetch receive the newest fetch's outcome.
- Garbage collection never removes an entry with observers, an in-flight fetch, or a fetch deferred while
  offline.

## Benchmarks

```bash
dotnet run -c Release --project benchmarks/DotNetQuery.Benchmarks           # all
dotnet run -c Release --project benchmarks/DotNetQuery.Benchmarks -- --filter *QueryKey*
```

See [benchmarks/DotNetQuery.Benchmarks/README.md](benchmarks/DotNetQuery.Benchmarks/README.md).

## NativeAOT / trimming

Core, DependencyInjection, Persistence (all providers), Http and GraphQL are `IsAotCompatible`. Verify with
the harness - a clean publish emits zero IL2xxx/IL3xxx
warnings and produces a native binary:

```bash
dotnet publish aot/DotNetQuery.AotHarness -c Release -r linux-x64   # or win-x64 / osx-arm64
```

See [aot/DotNetQuery.AotHarness/README.md](aot/DotNetQuery.AotHarness/README.md).
