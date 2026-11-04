# DotNetQuery.Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) micro-benchmarks for the performance-sensitive paths in
`DotNetQuery`.

## Running

Benchmarks **must** run in `Release`:

```bash
# All benchmarks
dotnet run -c Release --project tests/DotNetQuery.Benchmarks

# A subset (glob against type/method names)
dotnet run -c Release --project tests/DotNetQuery.Benchmarks -- --filter *QueryKey*

# Quick validation pass (single op, no statistics - not a real measurement)
dotnet run -c Release --project tests/DotNetQuery.Benchmarks -- --job dry

# List everything available
dotnet run -c Release --project tests/DotNetQuery.Benchmarks -- --list flat
```

## What is measured

| Suite | Path exercised |
| --- | --- |
| `QueryKeyBenchmarks` | Key construction, cached hash-code read, structural equality, and hierarchical `IsPrefixOf` matching - the operations the cache dictionary performs on every lookup. Parameterized by segment count. |
| `FetchBenchmarks` | `CachedHit` (fresh-cache fast path, the baseline) vs. `ColdFetch` (entry creation + full fetch cycle) vs. `ForcedRefetch` (re-running the cycle on a live entry). |
| `DedupBenchmarks` | Request deduplication: 2/8/32 concurrent callers for the same in-flight key collapsing onto one shared fetch. |
| `NotificationBenchmarks` | Fanning one state change out to 1/10/100 subscribed observers - the cost every `SetQueryData` and fetch completion pays. |
| `SelectorBenchmarks` | 100 plain observers vs 100 selection observers whose selected value does not change. |
| `StructuralSharingBenchmarks` | Refetching a 1,000-record list with sharing off/on, equal vs one item changed. |
| `TransactionBenchmarks` | Four separate atomic updates vs one four-key `SetQueriesData` transaction. |
| `CacheFootprintBenchmarks` | Allocation for caching 1,000 queries, and filtering them. |
| `CacheComparisonBenchmarks` | Serving a cached value: `ConcurrentDictionary` of tasks (floor), `MemoryCache.GetOrCreateAsync`, a DotNetQuery fresh hit. |
| `LargeUpdateBenchmarks` | Replacing a 10,000-item list observed by 100 observers; one transaction updating 100 keys. |
| `InfiniteQueryMemoryBenchmarks` | Loading 50 pages of 100 items into a cache-backed infinite query. |
| `PersistenceBenchmarks` | Persisting and restoring 100 queries (source-generated JSON, in-memory store). |
| `EvictionBenchmarks` | Garbage-collecting 10,000 expired inactive queries; trimming them to 100 (LRU). |
| `EventSyncBenchmarks` | Real-time event synchronization with 10 / 1,000 cached queries: one event end to end, a rejected duplicate, an entity upsert into a list and detail, and a coalesced burst of 100 invalidations. |
| `BlazorRenderBenchmarks` | Mounting `<Query>` over cached data, and the re-render a cache write triggers (through bUnit's renderer). |

## Baselines

- [Baselines/2026-11-02-shortrun.md](Baselines/2026-11-02-shortrun.md) - first committed baseline (ShortRun job,
  indicative).
- [Baselines/2026-11-03-shortrun.md](Baselines/2026-11-03-shortrun.md) - after the hot-path allocation work, with
  the new suites and the `MemoryCache` comparison.

Allocation regressions on the hottest paths are also caught by `AllocationBudgetTests` in the regular test suite,
which runs on every CI build (BenchmarkDotNet runs are too slow and noisy for that).

## Publishing a baseline

Commit the exported summary (`BenchmarkDotNet.Artifacts/results/*.md`) when establishing or updating the
1.0 baseline so regressions are reviewable in diffs.
