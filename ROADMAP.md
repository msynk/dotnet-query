# DotNetQuery — Path to 1.0

Status as of this milestone: **feature-complete core, 5 UI adapters, 130 tests (91% core coverage), 15 NuGet packages, one verified sample, a BenchmarkDotNet suite, a verified NativeAOT publish, a GitHub Actions CI pipeline, and a frozen public API surface.** All P1 items are complete; P2/P3/P4 breadth is largely landed. This document tracks what remains before a formal 1.0 tag.

## Done

- **Core engine** — `QueryClient`, `QueryCache`, `QueryKey`, immutable state/options, retry policies, request deduplication, observers, cancellation, background GC.
- **Mutations** — optimistic updates, rollback, retries, lifecycle callbacks, cancellation.
- **Infinite queries** — offset/cursor/bidirectional, sliding window, cache-integrated invalidation.
- **Background sync** — interval, manual, focus/reconnect/resume triggers, dependency invalidation.
- **Offline** — connectivity-aware pause/resume, offline mutation queue.
- **Persistence** — pluggable `IQueryPersister`; in-memory, file-system, SQLite, Redis, and Blazor-WASM `localStorage` providers.
- **Diagnostics** — event stream + OpenTelemetry `ActivitySource`/`Meter`; DevTools model + Blazor panel.
- **Adapters** — Blazor (`<Query>` + browser signals), MVVM, WPF, WinUI, Avalonia, MAUI.
- **Transport** — `DotNetQuery.Http`: `HttpClient` → fetcher helpers and fetch-JSON-into-cache conveniences.
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
| **IndexedDB persistence** | Blazor WASM provider for datasets beyond `localStorage`'s ~5 MB quota; needs a bundled JS module for the async cursor API. | M |
| **MAUI iOS/macCatalyst/Windows heads** | Adapter targets `net10.0-android` today (CLI-verified). Add the other heads in a Mac / full-VS build environment — no code changes (Essentials-only). | S |

### P3 — Samples & docs depth

| Item | Notes | Est. |
| --- | --- | --- |
| **More samples** | Brief lists Dashboard, ECommerce, Chat (infinite + SignalR), OfflineFirst. Each exercises a different feature cluster. | L |
| **Avalonia sample** | Desktop app mirroring TodoApp to demo the Avalonia adapter live. | M |
| **Advanced guides** | ✅ **Done** — [advanced-scenarios.md](docs/advanced-scenarios.md) covers persistence, cancellation policies, and testing your queries. Could still expand offline recipes further. | S |
| **Standalone DevTools app** | Optional out-of-process inspector consuming the `ExportJson` model. | L |

### P4 — Optional integrations

| Item | Notes |
| --- | --- |
| Transport helpers | ✅ **HTTP/JSON done** (`DotNetQuery.Http`). GraphQL / gRPC / SignalR conveniences remain as optional follow-ups. |
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
