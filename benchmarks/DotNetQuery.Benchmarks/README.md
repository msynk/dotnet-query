# DotNetQuery.Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org/) micro-benchmarks for the performance-sensitive paths in
`DotNetQuery.Core`.

## Running

Benchmarks **must** run in `Release`:

```bash
# All benchmarks
dotnet run -c Release --project benchmarks/DotNetQuery.Benchmarks

# A subset (glob against type/method names)
dotnet run -c Release --project benchmarks/DotNetQuery.Benchmarks -- --filter *QueryKey*

# Quick validation pass (single op, no statistics - not a real measurement)
dotnet run -c Release --project benchmarks/DotNetQuery.Benchmarks -- --job dry

# List everything available
dotnet run -c Release --project benchmarks/DotNetQuery.Benchmarks -- --list flat
```

## What is measured

| Suite | Path exercised |
| --- | --- |
| `QueryKeyBenchmarks` | Key construction, cached hash-code read, structural equality, and hierarchical `IsPrefixOf` matching - the operations the cache dictionary performs on every lookup. Parameterized by segment count. |
| `FetchBenchmarks` | `CachedHit` (fresh-cache fast path, the baseline) vs. `ColdFetch` (entry creation + full fetch cycle) vs. `ForcedRefetch` (re-running the cycle on a live entry). |
| `DedupBenchmarks` | Request deduplication: 2/8/32 concurrent callers for the same in-flight key collapsing onto one shared fetch. |
| `NotificationBenchmarks` | Fanning one state change out to 1/10/100 subscribed observers - the cost every `SetQueryData` and fetch completion pays. |

## Publishing a baseline

Commit the exported summary (`BenchmarkDotNet.Artifacts/results/*.md`) when establishing or updating the
1.0 baseline so regressions are reviewable in diffs.
