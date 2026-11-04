# Benchmark baseline - 2026-11-04 (ShortRun, indicative): real-time event synchronization

First baseline of `EventSyncBenchmarks` (the `QueryEventSynchronizer` pipeline). ShortRun uses 3 iterations, so the
Error column is wide: compare **Allocated** precisely and **Mean** only for large differences. Reproduce with:

```bash
dotnet run -c Release --project tests/DotNetQuery.Benchmarks -- --job short --filter "*EventSync*"
```

- `Dispatch_Update` - one event end to end through `ProcessAsync`: queue, metadata, duplicate check, mapping, atomic
  `Update<long>` of one query. The cost grows with `CachedQueries` because data mappings that match by key prefix scan
  the cache once per event.
- `Dispatch_Duplicate` - a redelivered event rejected by its id.
- `Dispatch_Upsert` - an entity upsert replacing one item of a 100-item list and its detail (cache scan included).
- `Burst_100_Invalidations` - per event of a 100-event burst (99 `Post` + one awaited): coalesced into one invalidation.

```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26100.4351/24H2/2024Update/HudsonValley)
Intel Core i9-14900K 3.20GHz, 1 CPU, 32 logical and 24 physical cores
.NET SDK 11.0.100-rc.3.26522.148
  [Host]   : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2
  ShortRun : .NET 10.0.14 (10.0.1426.52326), X64 RyuJIT AVX2

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                  | CachedQueries | Mean        | Error        | StdDev    | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------------ |-------------- |------------:|-------------:|----------:|------:|--------:|-------:|-------:|----------:|------------:|
| **Dispatch_Update**         | **10**            |  **2,576.1 ns** |    **364.15 ns** |  **19.96 ns** |  **1.00** |    **0.01** | **0.0229** | **0.0038** |    **1952 B** |        **1.00** |
| Dispatch_Duplicate      | 10            |  1,510.7 ns |    954.37 ns |  52.31 ns |  0.59 |    0.02 | 0.0057 |      - |     472 B |        0.24 |
| Dispatch_Upsert         | 10            |  3,214.7 ns |    618.69 ns |  33.91 ns |  1.25 |    0.01 | 0.0458 |      - |    3456 B |        1.77 |
| Burst_100_Invalidations | 10            |    275.1 ns |     87.31 ns |   4.79 ns |  0.11 |    0.00 | 0.0021 |      - |     186 B |        0.10 |
|                         |               |             |              |           |       |         |        |        |           |             |
| **Dispatch_Update**         | **1000**          | **18,986.9 ns** | **13,237.48 ns** | **725.59 ns** |  **1.00** |    **0.05** |      **-** |      **-** |    **1952 B** |        **1.00** |
| Dispatch_Duplicate      | 1000          |  1,447.3 ns |    330.30 ns |  18.10 ns |  0.08 |    0.00 | 0.0038 |      - |     472 B |        0.24 |
| Dispatch_Upsert         | 1000          | 28,457.5 ns |  1,653.08 ns |  90.61 ns |  1.50 |    0.05 |      - |      - |    3456 B |        1.77 |
| Burst_100_Invalidations | 1000          |    524.4 ns |    175.86 ns |   9.64 ns |  0.03 |    0.00 | 0.0031 |      - |     268 B |        0.14 |
