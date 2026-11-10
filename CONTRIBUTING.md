# Contributing to DotNetQuery

## Build & test

```bash
dotnet build DotNetQuery.slnx -c Release
dotnet test DotNetQuery.slnx -c Release
```

The full solution (including the WPF and WinUI adapters, which target Windows) builds on Windows. The
core and cross-platform adapters build and test on Linux/macOS too; CI runs both. Install the .NET 8, 9 and 10 SDKs
or targeting packs (the Uno adapter targets `net9.0`).

**Platform adapters.** `DotNetQuery.Maui` is **not** in the solution (its platform heads need workloads
and per-OS tooling). Build it directly:

```bash
dotnet build src/adapters/DotNetQuery.Maui -f net10.0-android      # needs the maui/android workloads
```

Its target heads are environment-gated, so each builds where it can:

- **Android** - `dotnet` CLI, VS, and CI.
- **Windows** (`net10.0-windows10.0.19041.0`) - only under full Visual Studio MSBuild (the head is gated
  on MSBuild runtime type `Full`, since the transitive WindowsAppSDK PRI/Appx tasks aren't in the bare
  SDK): `& "$(vswhere ... -find MSBuild\**\Bin\MSBuild.exe)" src/adapters/DotNetQuery.Maui -restore`.
- **iOS / Mac Catalyst** - added when building on macOS.

`DotNetQuery.WinUI` builds with the plain CLI because it excludes the WindowsAppSDK MSBuild targets (see
its README). `DotNetQuery.Uno` references only Uno's WinUI API (`Uno.WinUI`) and strips Uno's XAML source generators,
so it builds without the Uno.Sdk.

**Test projects.** `DotNetQuery.Tests` and `DotNetQuery.Blazor.Tests` target both `net8.0` and `net10.0` (the
libraries ship for both), so `dotnet test` runs every test twice; install both runtimes. `DotNetQuery.Desktop.Tests`
(Windows) drives the WPF lifecycle class with a real `System.Windows.Application` and tests the WinUI activation
mapping; `DotNetQuery.Uno.Tests` tests the Uno mapping. The Redis persistence and synchronization tests run against
`DOTNETQUERY_REDIS` (default `localhost:6379`) and skip without a server:
`docker run --rm -p 6379:6379 redis:7-alpine`. Coverage: `dotnet test --settings coverage.runsettings --collect:"XPlat Code Coverage"`. `AllocationBudgetTests`
fail when a hot path (cache hit, `GetQueryData`, `SetQueryData`, key creation) starts allocating noticeably more -
the measured cost is noted next to each budget; update it only together with the reason.

## Source generator

`src/DotNetQuery.SourceGenerators` (netstandard2.0, Roslyn 4.8) implements `[GenerateQueries]`. It is not a
package of its own: `DotNetQuery.csproj` packs its DLL under `analyzers/dotnet/cs`. Projects in this repository that
use it reference it as an analyzer:

```xml
<ProjectReference Include="..\..\src\DotNetQuery.SourceGenerators\DotNetQuery.SourceGenerators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

`tests/DotNetQuery.SourceGenerators.Tests` drives the generator directly (diagnostics, zero-warning output in a
documented nullable consumer, incremental caching); `GeneratedQueriesTests` in `DotNetQuery.Tests` exercises the
generated code at runtime. New diagnostics go into `AnalyzerReleases.Unshipped.md`.

## Documentation

Guides live in `docs/` and are also built into a website with an API reference by DocFX (see
[Documentation site](#documentation-site)); `.github/workflows/docs.yml` builds it on pull requests and publishes it to
GitHub Pages from `main`.

## Reporting problems

Bugs and feature requests: GitHub issues (templates provided). Security vulnerabilities: privately, see
[SECURITY.md](SECURITY.md). Everyone participating follows the [code of conduct](CODE_OF_CONDUCT.md).

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

Every shipped package is analyzer-enabled: `DotNetQuery`, `.Blazor`, `.Blazor.Server`, `.Persistence.Sqlite`,
`.Persistence.Redis`, `.Wpf`, `.WinUI`, `.Avalonia`, `.Maui`, `.Uno`. In the Razor adapter the Razor-generated
component members are recorded too (`RS0041`, oblivious nullability in generated `BuildRenderTree`, is suppressed).
Releases ship the baselines with `eng/prepare-release.ps1` (see [docs/releasing.md](docs/releasing.md)).

## Package layout

A feature gets its own package only when it brings a dependency some users will not want (a third-party
library or a UI platform). Everything that needs only the BCL and `Microsoft.Extensions.DependencyInjection`
/ `Options` lives in `DotNetQuery`, in a folder and namespace of its own (`Http/`, `GraphQL/`,
`Persistence/`, `Mvvm/`, `DevTools/`, `DependencyInjection/`); browser-only features live in
`DotNetQuery.Blazor`. Unused code is removed by the trimmer, so merging costs apps nothing.

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

`QueryEventSynchronizer` (real-time events) adds its own rules, covered by `QueryEventSynchronizerTests`,
`QueryEventRelayTests` and `tests/DotNetQuery.SignalR.Tests`:

- Events are processed by one loop in arrival order; the deduplication window and version tracker are touched only by
  that loop (work that must run in order with events is queued as an item, as `ChangeScope` does).
- An event id, sequence or version is recorded only after the event was handled (applied, reconciled by invalidation,
  filtered or discarded as stale).
- A transport callback only enqueues; cache work, recoveries and handlers never run on it.
- A recovery requested while one is pending coalesces with it; one requested while one runs runs again afterwards.
- Refetches started by the synchronizer must not inherit the loop's execution context (`ExecutionContext.SuppressFlow`).
- The synchronizer never disposes the client, nor a source or connection it does not own.

## Browser end-to-end tests

`tests/DotNetQuery.E2E` drives the `demo/TodoApp` Blazor app in a real browser with Playwright: prerender
hydration without a second fetch or loading flash, optimistic add and rollback, keep-previous-data filtering,
cross-tab sync, the WebAssembly and Auto render modes, streaming SSR, `EditForm`, the virtualized list, `QuickGrid`
and tenant switching.
The fixture launches the built demo in Kestrel and uses the locally installed Chrome (or Edge) through
Playwright's browser channels, so no browser download is needed; the tests are skipped when neither browser is
installed or the demo was not built. They run as part of `dotnet test DotNetQuery.slnx` (the Windows CI job
has Chrome).

## Benchmarks

```bash
dotnet run -c Release --project tests/DotNetQuery.Benchmarks           # all
dotnet run -c Release --project tests/DotNetQuery.Benchmarks -- --filter *QueryKey*
```

See [tests/DotNetQuery.Benchmarks/README.md](tests/DotNetQuery.Benchmarks/README.md).

## NativeAOT / trimming

The core (DependencyInjection, Persistence, Http, GraphQL included), the persistence providers, `DotNetQuery.Blazor`
and `DotNetQuery.Blazor.Server` are `IsAotCompatible`. Verify with the harness - a clean publish emits zero
IL2xxx/IL3xxx warnings and produces a native binary:

```bash
dotnet publish aot/DotNetQuery.AotHarness -c Release -r linux-x64   # or win-x64 / osx-arm64
```

See [aot/DotNetQuery.AotHarness/README.md](aot/DotNetQuery.AotHarness/README.md). For the Blazor adapter, CI publishes
`demo/TodoApp.Client` trimmed (`-p:SuppressTrimAnalysisWarnings=false -p:TrimmerSingleWarn=false`) and fails on
any trim warning from a DotNetQuery assembly.

## Documentation site

`docs/docfx.json` reads the API reference from the built assemblies in `artifacts/docs-bin` (DocFX cannot run the Razor
source generator, so building from the projects would miss the `.razor` components). `.github/workflows/docs.yml`
builds them first; locally:

```bash
for p in src/DotNetQuery src/adapters/DotNetQuery.Blazor src/adapters/DotNetQuery.Blazor.Server src/adapters/DotNetQuery.Avalonia \
         src/persistence/DotNetQuery.Persistence.Sqlite src/persistence/DotNetQuery.Persistence.Redis; do
  dotnet build $p -c Release -f net10.0 -p:CopyLocalLockFileAssemblies=true -p:OutDir=$PWD/artifacts/docs-bin/
done
docfx docs/docfx.json --serve
```
