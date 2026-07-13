# DotNetQuery — Path to 1.0

Status as of this milestone: **feature-complete core, 5 UI adapters, 130 tests (91% core coverage), 16 NuGet packages, seven verified samples (Blazor Server, MAUI/Android, Avalonia, Chat, OfflineFirst, ECommerce, Dashboard), a BenchmarkDotNet suite, a verified NativeAOT publish, a GitHub Actions CI pipeline, and a frozen public API surface.** All P1 items are complete; P2/P3/P4 breadth is largely landed. This document tracks what remains before a formal 1.0 tag.

## Done

- **Core engine** — `QueryClient`, `QueryCache`, `QueryKey`, immutable state/options, retry policies, request deduplication, observers, cancellation, background GC.
- **Mutations** — optimistic updates, rollback, retries, lifecycle callbacks, cancellation.
- **Infinite queries** — offset/cursor/bidirectional, sliding window, cache-integrated invalidation.
- **Background sync** — interval, manual, focus/reconnect/resume triggers, dependency invalidation.
- **Offline** — connectivity-aware pause/resume, offline mutation queue.
- **Persistence** — pluggable `IQueryPersister`; in-memory, file-system, SQLite, Redis, and Blazor-WASM (`localStorage` + IndexedDB) providers.
- **Diagnostics** — event stream + OpenTelemetry `ActivitySource`/`Meter`; DevTools model + Blazor panel.
- **Adapters** — Blazor (`<Query>` + browser signals), MVVM, WPF, WinUI, Avalonia, MAUI.
- **Transport** — `DotNetQuery.Http` (HTTP/JSON fetchers) and `DotNetQuery.GraphQL` (GraphQL-over-HTTP); plus `StreamIntoCacheAsync` in Core to push any `IAsyncEnumerable<T>` (SignalR/channels/gRPC streaming) into the cache in real time.
- **Query keys** — hierarchical `QueryKey.Append` and a fluent `QueryKeyBuilder` for safe, DRY key construction.
- **Infra** — DI integration, multi-target net8.0/net10.0, NuGet packaging with symbols + SourceLink, 9 doc guides.
- **Tests** — unit, integration, concurrency/stress, bUnit component; 91% core line coverage.
- **Benchmarks** — `DotNetQuery.Benchmarks` (BenchmarkDotNet): QueryKey hashing/equality, cold vs. cached fetch, dedup fan-out, observer-notification cost.
- **NativeAOT / trimming** — `DotNetQuery.AotHarness` publishes with `PublishAot=true` (warnings-as-errors) to a native binary with zero IL2xxx/IL3xxx warnings.
- **CI** — GitHub Actions: full build/test/pack on Windows, cross-platform test on Linux, and an AOT-publish gate.
- **`<Query>` refetch** — re-fetches on structural `Inputs` change, not just key change.
- **API freeze** — `Microsoft.CodeAnalysis.PublicApiAnalyzers` locks the public surface of all 7 non-Razor shipped libraries via committed `PublicAPI.*.txt` baselines; any surface change fails the build. See [CONTRIBUTING.md](CONTRIBUTING.md).
- **Cancellation-on-unmount** — opt-in `CancelInFlightWhenUnobserved` (per-query or client default) aborts a shared fetch when its last observer leaves.
- **SQLite / Redis / Blazor-WASM persistence** — `DotNetQuery.Persistence.Sqlite` (Microsoft.Data.Sqlite), `.Redis` (StackExchange.Redis, integration-tested against a Docker server), and `.BlazorWasm` (`IJSRuntime` `localStorage`).
- **WinUI & MAUI adapters** — `DotNetQuery.WinUI` (dispatcher factories) and `DotNetQuery.Maui` (dispatcher factories + `MauiConnectivityBridge` driving offline pause/resume from `Connectivity.Current`).
- **HTTP transport + structured keys** — `DotNetQuery.Http` fetcher helpers; `QueryKey.Append`/`QueryKeyBuilder`.
- **Docs** — added the "Advanced Scenarios" guide (persistence, HTTP, key building, cancellation, testing); 9 doc guides.

---

## Remaining work

### P1 — Should land before 1.0

**All P1 items are complete** and moved to **Done**: Benchmarks, NativeAOT verification, API surface review + freeze, `<Query>` `Inputs`-change refetch, and the CI workflow. The public API is locked; 1.0 packaging can proceed.

### P2 — Broaden reach

**Largely complete** — SQLite, Redis, and Blazor-WASM `localStorage` persistence providers, plus the
WinUI and MAUI adapters, all landed (see **Done**). Remaining threads:

| Item | Notes | Est. |
| --- | --- | --- |
| **MAUI iOS/macCatalyst heads** | The adapter now builds `net10.0-android` (CLI/VS/CI) and `net10.0-windows10.0.19041.0` (full VS MSBuild, verified). The Apple heads are wired to build on macOS — no code changes (Essentials-only), just need a Mac build host. | S |

_Landed this milestone: **MAUI Windows head** (`net10.0-windows10.0.19041.0`, verified with VS MSBuild; gated on the `Full` MSBuild runtime so `dotnet build`/CI stay Android-only). **IndexedDB persistence** (`IndexedDbQueryPersister` in `DotNetQuery.Persistence.BlazorWasm`, with a bundled JS static-web-asset module; C# logic tested against a fake `IJSRuntime`, browser side runs the shipped module)._

### P3 — Samples & docs depth

| Item | Notes | Est. |
| --- | --- | --- |
| **More samples** | ✅ **Done** — MAUI (emulator-verified), Chat (infinite + streaming), OfflineFirst (persist/offline queue/reconnect), ECommerce (optimistic updates + rollback), and Dashboard (dedup + background refresh); the four console samples are CI smoke tests. | — |
| **Avalonia sample** | ✅ **Done** — `samples/DotNetQuery.Sample.Avalonia`, with a headless-render self-check (`-- verify`) that runs as an end-to-end smoke test in CI. | M |
| **Advanced guides** | ✅ **Done** — [advanced-scenarios.md](docs/advanced-scenarios.md) covers persistence, cancellation policies, and testing your queries. Could still expand offline recipes further. | S |
| **Standalone DevTools app** | ✅ **Done** — `tools/DotNetQuery.DevTools.Cli`, an out-of-process console inspector that renders a `QueryDevTools.ExportJson()` dashboard (file/stdin/demo; CI smoke test). | S |

### P4 — Optional integrations

| Item | Notes |
| --- | --- |
| Transport helpers | ✅ **HTTP/JSON** (`DotNetQuery.Http`), **GraphQL** (`DotNetQuery.GraphQL`), and **real-time streaming** (`QueryStreamExtensions.StreamIntoCacheAsync`, which bridges any `IAsyncEnumerable<T>` — SignalR `StreamAsync`, channels, gRPC streaming — into the cache) all done. gRPC unary calls are already a `Task<T>`, so need no helper. |
| ~~Query cancellation-on-unmount policies~~ | ✅ **Done** — `CancelInFlightWhenUnobserved` (per-query and client default). |
| ~~Structured query-key builders~~ | ✅ **Done** — `QueryKey.Append` + fluent `QueryKeyBuilder`. A compile-time source generator remains a possible future. |

---

## Known characteristics (documented, not bugs)

- A single `Mutation` instance is stateful/sequential; use one-shot `client.MutateAsync` for parallel independent mutations.
- A brand-new query's first synchronous render is `Idle`, not `Loading` (the `Loading` transition is deferred one scheduler hop for deduplication correctness). Bind first-load spinners to `IsPending`, which covers both.
- Infinite queries must be disposed to unregister from invalidation tracking (they're held by weak reference).

## Suggested sequence

1. ~~Benchmarks + AOT verification (proves the performance claims).~~ **Done.** Run `dotnet run -c Release --project benchmarks/DotNetQuery.Benchmarks` and commit the exported summary as the published 1.0 baseline.
2. ~~API freeze + `<Query>` param-change fix (locks correctness before packaging 1.0).~~ **Done.**
3. ~~CI + SourceLink (once a git remote exists).~~ **Done.**
4. **Now: breadth** — additional persistence providers, WinUI/MAUI adapters, more samples (all P2/P3).
