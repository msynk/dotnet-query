using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DotNetQuery.Sample.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Construct the client and view model on the UI thread (the adapter factory requires it).
        var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromSeconds(5) });
        var viewModel = new MainWindowViewModel(client, new TodoService());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
