# DotNetQuery.Maui

.NET MAUI adapter for DotNetQuery: dispatcher-aware MVVM binding factories plus a connectivity bridge
that drives offline pause/resume from `Connectivity.Current`.

```csharp
using DotNetQuery.Maui;

// Dispatcher-aware factories (call on the MAUI main thread):
var todos = client.CreateObservableQuery(
    QueryKey.Create("todos"),
    ct => api.GetTodosAsync(ct));

// Wire device connectivity into DotNetQuery's offline handling (keep it alive for the app's lifetime):
var connectivity = serviceProvider.GetRequiredService<ConnectivityProvider>();
var bridge = new MauiConnectivityBridge(connectivity);
// queries now pause when the device goes offline and refetch on reconnect; Dispose to unsubscribe.
```

Register the client with `AddDotNetQuery()` (from `DotNetQuery.DependencyInjection`) in your
`MauiProgram`, resolve the `ConnectivityProvider`, and construct the bridge once at startup.

## Target frameworks

The committed project targets **`net10.0-android`** — the head that builds with the .NET CLI on a
Windows/Linux dev box and in CI. To ship the other platforms, add their TFMs in the appropriate build
environment:

- `net10.0-ios;net10.0-maccatalyst` — build on a Mac.
- `net10.0-windows10.0.19041.0` — build with full Visual Studio MSBuild (the transitive WinUI PRI/Appx
  MSBuild tasks aren't in the bare .NET SDK).

The adapter uses only **MAUI Essentials** (`MainThread`, `Connectivity`), which supports every MAUI
platform, so enabling the additional heads needs no code changes.

## Focus / resume

DotNetQuery refetches stale active queries on focus and app-resume via its signal managers
(`IFocusManager`, `IResumeManager`). Wire your app lifecycle to them (e.g. from
`Window.Activated` / `Window.Resumed`, or `ConfigureLifecycleEvents`) by calling `Signal()` on the
resolved managers.
