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

Register the client with `AddDotNetQuery()` (from the `DotNetQuery` package) in your
`MauiProgram`, resolve the `ConnectivityProvider`, and construct the bridge once at startup.

## Target frameworks

The project's target heads are selected by build environment so each builds where it can:

| Head | Built by | Notes |
| --- | --- | --- |
| `net10.0-android` | `dotnet` CLI, VS, and CI | always on |
| `net10.0-windows10.0.19041.0` | **full Visual Studio MSBuild** on Windows | added only under MSBuild runtime type `Full` - the transitive WindowsAppSDK PRI/Appx tasks aren't in the bare .NET SDK, so `dotnet build` (runtime type `Core`) skips this head |
| `net10.0-ios;net10.0-maccatalyst` | a Mac | added when building on macOS |

So `dotnet build` / CI build Android only (no breakage), while `MSBuild.exe` from Visual Studio also
builds the Windows head, and a Mac build adds the Apple heads. The adapter uses only **MAUI Essentials**
(`MainThread`, `Connectivity`), which supports every MAUI platform, so no code changes are needed per head.

## Focus, resume and connectivity

`MauiQueryLifecycle` wires a window's `Activated` / `Deactivated` / `Resumed` / `Stopped` events and the device
connectivity (through `MauiConnectivityBridge`) into the client, so stale on-screen queries refetch when the user
returns to the app and queries pause while the device is offline:

```csharp
protected override Window CreateWindow(IActivationState? activationState)
{
    var window = new Window(new AppShell());
    var lifecycle = new MauiQueryLifecycle(window, _services.GetRequiredService<AppLifecycleSignals>());
    window.Destroying += (_, _) => lifecycle.Dispose();
    return window;
}
```

## Persistence

`MauiQueryPersistence.CreateFileSystemPersister()` returns a `FileSystemQueryPersister` in the app's private data
directory (`FileSystem.AppDataDirectory`). See [Desktop and mobile](../../../docs/desktop-and-mobile.md) for the
full guide (MVVM commands, infinite queries, lifecycle on every platform).
