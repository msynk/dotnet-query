# Desktop and mobile (WPF, WinUI, Avalonia, MAUI)

The core package works in any .NET UI stack. The four XAML adapters add the same three things on each platform:
dispatcher-aware factories for the MVVM bindings, a lifecycle class that turns the platform's window and
application events into refetches, and (for MAUI) device connectivity and storage helpers.

| Package | Factories | Lifecycle | Connectivity |
| --- | --- | --- | --- |
| `Msynk.DotNetQuery.Wpf` | `WpfQueryExtensions` | `WpfQueryLifecycle(Application, ...)` - activation, system resume | OS network availability |
| `Msynk.DotNetQuery.WinUI` | `WinUIQueryExtensions` | `WinUIQueryLifecycle(Window, ...)` - window activation | OS network availability |
| `Msynk.DotNetQuery.Avalonia` | `AvaloniaQueryExtensions` | `AvaloniaQueryLifecycle(Window, ...)` (desktop) or `(Application, ...)` (mobile, macOS) | OS network availability |
| `Msynk.DotNetQuery.Maui` | `MauiQueryExtensions` | `MauiQueryLifecycle(Window, ...)` - activation, resume | `Connectivity.Current` (`MauiConnectivityBridge`) |

## Register the client

```csharp
// MauiProgram.cs, or the host builder of a WPF / WinUI / Avalonia app
services.AddDotNetQuery(options =>
{
    options.DefaultStaleTime = TimeSpan.FromSeconds(30);
    options.DefaultShouldRetry = HttpRetryPolicy.ShouldRetry;
});
```

A desktop or mobile app has one user, so the default singleton client is right. `AddDotNetQuery` also registers the
signal managers (`FocusManager`, `ResumeManager`, `ConnectivityProvider`) and an `AppLifecycleSignals` wired to
them; the lifecycle classes below report to it. Without DI, create the managers yourself and pass them to both:

```csharp
var focus = new FocusManager();
var resume = new ResumeManager();
var connectivity = new ConnectivityProvider();
var client = new QueryClient(options, focusManager: focus, resumeManager: resume, connectivity: connectivity);
var signals = new AppLifecycleSignals(focus, resume, connectivity);
```

## Bind queries and mutations

The factories create the bindings and assert that they are called on the UI thread (the bindings capture its
`SynchronizationContext` and apply every state change there, so bound values always match the notification that
announced them).

```csharp
public sealed class TodosViewModel : IDisposable
{
    public TodosViewModel(IQueryClient client, TodoApi api)
    {
        Todos = client.CreateObservableQuery(TodoQueries.List(api));
        Feed = client.CreateObservableInfiniteQuery(TodoQueries.FeedKey, api.GetPageAsync, TodoQueries.FeedOptions);
        Add = client.CreateObservableMutation<string, Todo>(api.AddAsync, new() { Invalidates = [TodoQueries.All] });
    }

    public ObservableQuery<List<Todo>> Todos { get; }
    public ObservableInfiniteQuery<int, TodoPage> Feed { get; }
    public ObservableMutation<string, Todo> Add { get; }

    public void Dispose() { Todos.Dispose(); Feed.Dispose(); Add.Dispose(); }
}
```

```xml
<ListView ItemsSource="{Binding Todos.Data}" />
<ProgressBar IsIndeterminate="True" IsVisible="{Binding Todos.IsFetching}" />
<Button Content="Refresh" Command="{Binding Todos.RefetchCommand}" />

<Button Content="Load more" Command="{Binding Feed.FetchNextPageCommand}" />

<TextBox x:Name="Title" />
<Button Content="Add" Command="{Binding Add.MutateCommand}" CommandParameter="{Binding Text, ElementName=Title}" />
<TextBlock Text="{Binding Add.Error.Message}" />
```

- **`ObservableQuery<T>`** - `Data`, `Status`, `IsPending`, `IsFetching`, `IsError`, `Error`, `IsPlaceholderData`, ...;
  `RefetchCommand`; `ChangeQuery(definition, keepPreviousData)` when an input such as a selected id changes.
- **`ObservableInfiniteQuery<TParam, TPage>`** - `Pages`, `HasNextPage`, `IsFetchingNextPage`, ...;
  `FetchNextPageCommand` / `FetchPreviousPageCommand` are disabled while there is no page to load or one is loading.
- **`ObservableMutation<TVariables, TData>`** - `IsPending`, `IsError`, `Data`, ...; `MutateCommand` takes the
  command parameter as the variables and is disabled while the mutation runs (a double-click submits once);
  `ResetCommand` dismisses a result or error.

Only the properties whose values changed raise `PropertyChanged`, so list controls are not rebuilt (and lose their
scroll position) when only a status flag flips. Command failures never reach the UI thread: they show up in `Error`.

## Refetch when the user comes back

Create the lifecycle class once at startup and dispose it on exit:

```csharp
// WPF - App.xaml.cs
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);
    _lifecycle = new WpfQueryLifecycle(this, Services.GetRequiredService<AppLifecycleSignals>());
}

// WinUI - App.xaml.cs OnLaunched
_window = new MainWindow();
_lifecycle = new WinUIQueryLifecycle(_window, Services.GetRequiredService<AppLifecycleSignals>());

// Avalonia - App.axaml.cs OnFrameworkInitializationCompleted
_lifecycle = ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
    ? new AvaloniaQueryLifecycle(desktop.MainWindow!, signals)
    : new AvaloniaQueryLifecycle(this, signals);

// MAUI - App.xaml.cs
protected override Window CreateWindow(IActivationState? activationState)
{
    var window = new Window(new AppShell());
    var lifecycle = new MauiQueryLifecycle(window, Services.GetRequiredService<AppLifecycleSignals>());
    window.Destroying += (_, _) => lifecycle.Dispose();
    return window;
}
```

What each one reports:

- **Focus** - the application comes back to the foreground after another application had it. Stale queries that are
  on screen refetch (`QueryOptions.RefetchOnFocus`). The first activation at startup and switching between the
  application's own windows do not count.
- **Resume** - a mobile application returns from the background, or a desktop machine wakes from sleep (WPF).
  Stale on-screen queries refetch (`RefetchOnResume`); an activation reported right after a resume is not counted
  twice.
- **Connectivity** - while the network is down, queries and mutations in the default `NetworkMode.Online` pause
  instead of failing; when it returns they resume and stale on-screen queries refetch (`RefetchOnReconnect`).
  Desktop adapters report the operating system's network availability (`NetworkConnectivityMonitor`, which says
  whether any interface is up, not whether your server is reachable); MAUI uses `Connectivity.Current`. Pass
  `monitorNetwork: false` (`monitorConnectivity: false` on MAUI) to report connectivity yourself through
  `ConnectivityProvider.SetOnline`.

Other UI stacks (Uno, console UIs, game engines) call `AppLifecycleSignals.NotifyActivated`, `NotifyDeactivated`,
`NotifyResumed` and `NotifyNetworkAvailability` from their own events.

## Keep the cache across restarts

`FileSystemQueryPersister` stores one file per query; the [offline guide](offline-mode.md) explains registration,
restore and the durable mutation queue. On MAUI, put it in the application's private data directory:

```csharp
var persister = MauiQueryPersistence.CreateFileSystemPersister();          // FileSystem.AppDataDirectory/dotnetquery
var protectedPersister = new ProtectedQueryPersister(persister, Protect, Unprotect); // encrypt sensitive data at rest
```

On desktop, use a folder under `Environment.SpecialFolder.LocalApplicationData`. The SQLite persister
(`Msynk.DotNetQuery.Persistence.Sqlite`) works on every platform as well.

## Verified

- Avalonia: CI runs the sample headless (`dotnet run --project samples/DotNetQuery.Sample.Avalonia -- verify`): the
  window renders the query, the bound `RefetchCommand` refetches, and re-activating the window refetches the stale
  query through `AvaloniaQueryLifecycle`.
- MAUI: the adapter and the sample build for Android in CI; iOS and Mac Catalyst need a Mac build host.
- WPF and WinUI: the adapters build in CI; their lifecycle classes subscribe to the platform events listed above
  and forward them to `AppLifecycleSignals`, whose behaviour is unit-tested.
