# DotNetQuery — Path to 1.0

Status as of this milestone: **feature-complete core, 3 UI adapters, 99 tests (91% core coverage), 9 dual-TFM NuGet packages, one verified sample.** This document tracks what remains before a formal 1.0 tag.

## Done

- **Core engine** — `QueryClient`, `QueryCache`, `QueryKey`, immutable state/options, retry policies, request deduplication, observers, cancellation, background GC.
- **Mutations** — optimistic updates, rollback, retries, lifecycle callbacks, cancellation.
- **Infinite queries** — offset/cursor/bidirectional, sliding window, cache-integrated invalidation.
- **Background sync** — interval, manual, focus/reconnect/resume triggers, dependency invalidation.
- **Offline** — connectivity-aware pause/resume, offline mutation queue.
- **Persistence** — pluggable `IQueryPersister`; in-memory + file-system providers.
- **Diagnostics** — event stream + OpenTelemetry `ActivitySource`/`Meter`; DevTools model + Blazor panel.
- **Adapters** — Blazor (`<Query>` + browser signals), MVVM, WPF, Avalonia.
- **Infra** — DI integration, multi-target net8.0/net10.0, NuGet packaging with symbols, 8 doc guides.
- **Tests** — unit, integration, concurrency/stress, bUnit component; 91% core line coverage.

---

## Remaining work

### P1 — Should land before 1.0

| Item | Notes | Est. |
| --- | --- | --- |
| **Benchmarks** (`DotNetQuery.Benchmarks`) | BenchmarkDotNet project for QueryKey hashing/equality, cold vs. cached fetch, dedup fan-out, notification cost. Publish baseline numbers. Brief's last untouched testing category. | M |
| **NativeAOT / trimming verification** | Core is annotated `IsAotCompatible`; actually `dotnet publish -p:PublishAot=true` a console harness and confirm zero trim/AOT warnings. | S |
| **API surface review + freeze** | Audit public types for seal/`internal`, naming, nullability consistency; run a PublicAPI analyzer; lock the surface. | M |
| **`<Query>` parameter-change refetch** | Re-fetch when the fetcher/inputs (not just key) change. Currently only key change re-subscribes. | S |
| **CI workflow** | GitHub Actions: restore/build/test/pack matrix on push + PR. Requires a real git remote (this workspace is not a git repo). | S |

### P2 — Broaden reach

| Item | Notes | Est. |
| --- | --- | --- |
| **More persistence providers** | Brief lists LocalStorage/IndexedDB (Blazor WASM via JS interop), SQLite, Redis. Each is an `IQueryPersister` implementation in its own package. | L |
| **WinUI adapter** | `DotNetQuery.WinUI` — thin dispatcher wrapper like WPF/Avalonia. Needs the Windows App SDK workload. | S |
| **MAUI adapter** | `DotNetQuery.Maui` — dispatcher + connectivity (`Connectivity.Current`) + lifecycle (focus/resume) wiring. Needs the MAUI workload. | M |
| **SourceLink** | Add `Microsoft.SourceLink.GitHub`, `PublishRepositoryUrl`, deterministic CI build. Requires a git remote. | S |

### P3 — Samples & docs depth

| Item | Notes | Est. |
| --- | --- | --- |
| **More samples** | Brief lists Dashboard, ECommerce, Chat (infinite + SignalR), OfflineFirst. Each exercises a different feature cluster. | L |
| **Avalonia sample** | Desktop app mirroring TodoApp to demo the Avalonia adapter live. | M |
| **Advanced guides** | "Advanced Scenarios" + "Testing your queries" guides; expand offline/persistence recipes. | M |
| **Standalone DevTools app** | Optional out-of-process inspector consuming the `ExportJson` model. | L |

### P4 — Optional integrations

| Item | Notes |
| --- | --- |
| Transport helpers | Thin conveniences for GraphQL / gRPC / SignalR fetchers (the core is transport-agnostic; these are ergonomics). |
| Query cancellation-on-unmount policies | Configurable behavior when the last observer leaves during an in-flight fetch. |
| Structured query-key builders | Optional typed key helpers / source generator for compile-time key safety. |

---

## Known characteristics (documented, not bugs)

- A single `Mutation` instance is stateful/sequential; use one-shot `client.MutateAsync` for parallel independent mutations.
- A brand-new query's first synchronous render is `Idle`, not `Loading` (the `Loading` transition is deferred one scheduler hop for deduplication correctness). Bind first-load spinners to `IsPending`, which covers both.
- Infinite queries must be disposed to unregister from invalidation tracking (they're held by weak reference).

## Suggested sequence

1. Benchmarks + AOT verification (proves the performance claims).
2. API freeze + `<Query>` param-change fix (locks correctness before packaging 1.0).
3. CI + SourceLink (once a git remote exists).
4. Then breadth: additional persistence providers, WinUI/MAUI adapters, more samples.
