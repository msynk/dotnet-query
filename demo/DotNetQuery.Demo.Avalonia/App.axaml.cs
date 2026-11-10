using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DotNetQuery.Avalonia;

namespace DotNetQuery.Sample.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Only the desktop lifetime gets a window here; the headless "verify" mode (no lifetime) builds its
        // own client and window in Program.HeadlessVerify.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Construct the client and view model on the UI thread (the adapter factory requires it). The signal
            // managers let the window's lifecycle drive refetches: switching back to the app refetches stale data,
            // and losing the network pauses queries until it returns.
            var focus = new FocusManager();
            var resume = new ResumeManager();
            var connectivity = new ConnectivityProvider();
            var client = new QueryClient(
                new QueryClientOptions { DefaultStaleTime = TimeSpan.FromSeconds(5) },
                focusManager: focus,
                resumeManager: resume,
                connectivity: connectivity);
            var viewModel = new MainWindowViewModel(client, new TodoService());
            var window = new MainWindow { DataContext = viewModel };
            var lifecycle = new AvaloniaQueryLifecycle(window, new AppLifecycleSignals(focus, resume, connectivity));
            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                lifecycle.Dispose();
                viewModel.Todos.Dispose();
                client.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
