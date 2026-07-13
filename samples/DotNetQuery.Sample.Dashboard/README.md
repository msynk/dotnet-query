# DotNetQuery.Sample.Dashboard

A console showcase of two DotNetQuery behaviors a dashboard relies on, with no UI-framework noise:

1. **Request deduplication** — [Program.cs](Program.cs) fires four widget requests at once, two of them
   for the same `[users]` key; the server sees a single `users` call because concurrent requests for one
   key share a single in-flight fetch.
2. **Background refresh** — an observed `[visitors]` query auto-polls on a `RefetchInterval`, so its value
   updates in the background without any manual refetch.

[DashboardApi](DashboardApi.cs) stands in for a metrics backend and counts its calls to prove the dedup.

## Run it

```bash
dotnet run --project samples/DotNetQuery.Sample.Dashboard
```

Expected: three widgets load (with the two `[users]` requests deduplicated to one server call), then the
`[visitors]` metric ticks up on its own via background polling. Deterministic and exits `0` on success, so
it also runs as an end-to-end smoke test in CI.
