# DotNetQuery.Uno

Uno Platform adapter for DotNetQuery: the same dispatcher-aware MVVM factories as the WinUI adapter
(`CreateObservableQuery`, `CreateObservableInfiniteQuery`, `CreateObservableMutation`) and `UnoQueryLifecycle`, which
refetches stale on-screen data when the window is activated again or the app returns from the background, and pauses
queries while the device is offline - on every Uno target (desktop, mobile, WebAssembly).

```csharp
using DotNetQuery.Uno;

// App.xaml.cs OnLaunched, with services.AddDotNetQuery() registered:
MainWindow = new Window();
_lifecycle = new UnoQueryLifecycle(MainWindow, this, Host.Services.GetRequiredService<AppLifecycleSignals>());

// In a view model created on the UI thread:
Todos = client.CreateObservableQuery(TodoQueries.List(api));
```

- **Focus** - `Window.Activated` after a deactivation refetches stale active queries (`RefetchOnFocus`).
- **Resume** - `Application.LeavingBackground` after `EnteredBackground` (mobile, WebAssembly tab switches) refetches
  them (`RefetchOnResume`).
- **Connectivity** - on desktop and mobile the operating system's network availability drives `ConnectivityProvider`;
  in the browser report it yourself with `ConnectivityProvider.SetOnline` (for example from `navigator.onLine`).

The package targets `net9.0` and `net10.0` (the frameworks Uno 6 supports) and references only Uno's WinUI API, so it
works with any Uno.Sdk app head. See [Desktop and mobile](../../../docs/desktop-and-mobile.md).
