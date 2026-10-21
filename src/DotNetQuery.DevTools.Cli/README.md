# DotNetQuery.DevTools.Cli

A standalone, **out-of-process** DevTools inspector. It consumes the JSON produced by any app's
[`QueryDevTools.ExportJson()`](../DotNetQuery/DevTools/QueryDevTools.cs) and renders a console
dashboard - cache stats, a queries table, and the recent event log - with no dependency on the running
app.

## Usage

```bash
# Inspect an export produced by your app (devtools.ExportJson() written to a file):
dotnet run --project src/DotNetQuery.DevTools.Cli -- export.json

# Or pipe one in on stdin:
your-app --dump-devtools | dotnet run --project src/DotNetQuery.DevTools.Cli -- -

# Emit a demo export (to capture a sample, or pipe straight back in):
dotnet run --project src/DotNetQuery.DevTools.Cli -- emit > export.json

# No argument: render a generated demo export (also the CI smoke test).
dotnet run --project src/DotNetQuery.DevTools.Cli
```

Example output:

```
== Cache ==================================================
  queries: 3   active: 1   stale: 3
  fetches: 4 started / 3 ok / 1 failed      mutations: 1 started / 1 done
  avg query duration: 0.2 ms
  status: Success=2  Error=1

== Queries ================================================
  KEY                    STATUS      TYPE         DATA  STALE  OBS  FETCH  UPDATED
  ["flaky"]              Error       Int32        no    yes    0    1      -
      └─ error: upstream 500
  ...
```

The tool deserializes only the export's JSON contract (mirror DTOs in [Export.cs](Export.cs)), so it stays
decoupled from the app that produced it.
