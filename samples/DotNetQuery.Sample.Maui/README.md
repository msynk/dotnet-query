# DotNetQuery.Sample.Maui

A minimal .NET MAUI app demonstrating the [`DotNetQuery.Maui`](../../src/adapters/DotNetQuery.Maui) adapter
end to end: it registers the query client with `AddDotNetQuery`, creates an `ObservableQuery` via the
adapter's dispatcher-aware factory, binds it straight to XAML through `INotifyPropertyChanged`, and wires
device connectivity with `MauiConnectivityBridge`.

[MainPage](MainPage.xaml.cs) shows a to-do list with a live status label and a **Refetch** button that
invalidates the query so the active observer refetches (a per-fetch counter makes it visible).

## Run it (Android)

The project targets `net10.0-android` (the head verified with the .NET CLI + an emulator).

```bash
# with an Android emulator running (or a device attached):
dotnet build samples/DotNetQuery.Sample.Maui -f net10.0-android -t:Run
```

Verified on a Pixel 7 (API 36) emulator: the list renders `fetch #1` on load, and tapping **Refetch**
updates it to `fetch #2`. (On a software-GPU emulator `adb screencap` may come back blank even though the
app renders - dump the view tree with `adb shell uiautomator dump` to see the actual on-screen text.)

To target more platforms, add `net10.0-ios;net10.0-maccatalyst` (on a Mac) or
`net10.0-windows10.0.19041.0` (with full Visual Studio MSBuild) to `<TargetFrameworks>`.
