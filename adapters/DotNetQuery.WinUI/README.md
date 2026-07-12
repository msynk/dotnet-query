# DotNetQuery.WinUI

WinUI 3 / Windows App SDK adapter for DotNetQuery. Dispatcher-aware factories over the MVVM bindings
(`ObservableQuery` / `ObservableMutation`) for XAML data binding — mirrors the WPF and Avalonia adapters.

```csharp
using DotNetQuery.WinUI;

// On the WinUI UI thread (e.g. in a page/view-model constructed on the UI thread):
var todos = client.CreateObservableQuery(
    QueryKey.Create("todos"),
    ct => api.GetTodosAsync(ct));

var addTodo = client.CreateObservableMutation<NewTodo, Todo>(
    (todo, ct) => api.CreateTodoAsync(todo, ct));
```

Bind `todos.Data`, `todos.IsPending`, `todos.IsError`, etc. in XAML; state changes marshal onto the
WinUI `DispatcherQueue`. The factories throw if called off the UI thread.

## Build note

This is a pure-C# library that uses only the `DispatcherQueue` projection from the Windows App SDK. It
references `Microsoft.WindowsAppSDK` with `ExcludeAssets="build;buildTransitive;buildMultitargeting"` so
it builds with the plain .NET CLI (`dotnet build`) — the excluded MSIX/PRI MSBuild tasks require full
Visual Studio tooling and aren't needed for a library with no XAML resources.
