using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

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
            // Construct the client and view model on the UI thread (the adapter factory requires it).
            var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromSeconds(5) });
            var viewModel = new MainWindowViewModel(client, new TodoService());
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) =>
            {
                viewModel.Todos.Dispose();
                client.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
