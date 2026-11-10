# DotNetQuery.Demo.Wpf

A minimal WPF app over the [`DotNetQuery.Wpf`](../../src/adapters/DotNetQuery.Wpf) adapter, in one file
([Program.cs](Program.cs)): the client is registered with `AddDotNetQuery`, the view model creates an
`ObservableQuery` through the adapter's dispatcher-aware factory, the window binds a list to `Todos.Data`, a status
line to `Todos.Status` and a **Refresh** button to `Todos.RefetchCommand`, and `WpfQueryLifecycle` refetches the
stale list when the user switches back to the app.

## Run it

```bash
dotnet run --project demo/DotNetQuery.Demo.Wpf            # the window
dotnet run --project demo/DotNetQuery.Demo.Wpf -- verify  # self-check (exit 0 == pass)
```

`verify` shows the window off-screen, checks that the bound list renders `fetch #1`, that the bound
`RefetchCommand` loads `fetch #2`, and that deactivating and re-activating the application (the
`Application.Deactivated`/`Activated` events `WpfQueryLifecycle` listens to) loads `fetch #3`. It runs in the Windows
CI job.
