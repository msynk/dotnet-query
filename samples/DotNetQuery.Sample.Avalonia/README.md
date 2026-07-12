# DotNetQuery.Sample.Avalonia

A minimal Avalonia desktop app demonstrating the [`DotNetQuery.Avalonia`](../../adapters/DotNetQuery.Avalonia)
adapter end to end: [MainWindowViewModel](MainWindowViewModel.cs) creates an `ObservableQuery` via the
adapter's dispatcher-aware factory, and [MainWindow.axaml](MainWindow.axaml) binds straight to
`Todos.Data`, `Todos.Status`, etc. through `INotifyPropertyChanged`. The **Refetch** button invalidates
the query so the active observer refetches (a per-fetch counter makes it visible).

## Run it

```bash
dotnet run --project samples/DotNetQuery.Sample.Avalonia          # normal desktop window
dotnet run --project samples/DotNetQuery.Sample.Avalonia -- verify  # headless self-check (no display)
```

The **`verify`** mode renders the window with Avalonia's headless Skia backend (no display required),
pumps the query to completion, triggers a refetch, asserts the data reached `Success` and updated from
`fetch #1` to `fetch #2`, saves a rendered `avalonia-verify.png`, and exits `0` on success. It runs as an
end-to-end smoke test in CI.
