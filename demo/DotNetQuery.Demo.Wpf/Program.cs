using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DotNetQuery.Mvvm;
using DotNetQuery.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetQuery.Sample.Wpf;

/// <summary>
/// A minimal WPF app over the WPF adapter: an <see cref="ObservableQuery{T}"/> bound to a list, a status line and a
/// Refresh button (<see cref="ObservableQuery{T}.RefetchCommand"/>), with <see cref="WpfQueryLifecycle"/> refetching stale
/// data when the user switches back to the app. Run with <c>verify</c> for the CI self-check.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddDotNetQuery(o => o.DefaultStaleTime = TimeSpan.Zero);
        services.AddSingleton<TodoService>();
        using var provider = services.BuildServiceProvider();

        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var viewModel = new MainViewModel(provider.GetRequiredService<IQueryClient>(), provider.GetRequiredService<TodoService>());
        var window = MainWindow.Create(viewModel);
        using var lifecycle = new WpfQueryLifecycle(app, provider.GetRequiredService<AppLifecycleSignals>(), monitorNetwork: !args.Contains("verify"));

        if (args.Contains("verify"))
        {
            return Verify(app, window, viewModel);
        }

        return app.Run(window);
    }

    // Shows the window off-screen, then checks: the bound list renders fetch #1, the bound RefetchCommand loads #2, and
    // switching away and back (Application.Deactivated/Activated, which WpfQueryLifecycle forwards) loads #3.
    private static int Verify(App app, Window window, MainViewModel viewModel)
    {
        window.Left = -10000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();

        Pump(() => viewModel.Todos.HasData);
        var list = (ListBox)window.FindName("TodoList");
        var rendered = list.Items.Cast<object>().Select(i => i?.ToString() ?? "").ToList();
        Console.WriteLine($"[verify] status={viewModel.Todos.Status}, rows=[{string.Join(" | ", rendered)}]");

        viewModel.Todos.RefetchCommand.Execute(null);
        Pump(() => viewModel.Todos.Data?.Any(t => t.Contains("#2")) == true);
        var commandRefetched = viewModel.Todos.Data?.Any(t => t.Contains("#2")) == true;
        Console.WriteLine($"[verify] after RefetchCommand rows=[{string.Join(" | ", viewModel.Todos.Data ?? [])}]");

        app.SimulateActivation();   // startup activation: recorded, not a refetch
        app.SimulateDeactivation();
        app.SimulateActivation();
        Pump(() => viewModel.Todos.Data?.Any(t => t.Contains("#3")) == true);
        var focusRefetched = viewModel.Todos.Data?.Any(t => t.Contains("#3")) == true;
        Console.WriteLine($"[verify] after switching back rows=[{string.Join(" | ", viewModel.Todos.Data ?? [])}]");

        var ok = viewModel.Todos.IsSuccess && rendered.Any(r => r.Contains("fetch #1")) && commandRefetched && focusRefetched;
        Console.WriteLine(ok ? "[verify] PASS" : "[verify] FAIL");
        window.Close();
        viewModel.Todos.Dispose();
        return ok ? 0 : 1;
    }

    private static void Pump(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }
}

/// <summary>The application; exposes its activation events for the self-check.</summary>
internal sealed class App : Application
{
    public void SimulateActivation() => OnActivated(EventArgs.Empty);

    public void SimulateDeactivation() => OnDeactivated(EventArgs.Empty);
}

/// <summary>A stand-in backend whose responses show how many times it was called.</summary>
internal sealed class TodoService
{
    private int _calls;

    public async Task<List<string>> GetTodosAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(50, cancellationToken);
        var call = Interlocked.Increment(ref _calls);
        return [$"Buy milk (fetch #{call})", $"Ship DotNetQuery (fetch #{call})"];
    }
}

/// <summary>The view model: one observable query, created on the UI thread through the adapter's factory.</summary>
internal sealed class MainViewModel(IQueryClient client, TodoService service)
{
    public ObservableQuery<List<string>> Todos { get; } = client.CreateObservableQuery(QueryKey.Create("todos"), service.GetTodosAsync);
}

/// <summary>The window, built in code to keep the sample to one file.</summary>
internal static class MainWindow
{
    public static Window Create(MainViewModel viewModel)
    {
        var list = new ListBox { Name = "TodoList", Height = 120 };
        list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Todos.Data"));
        var status = new TextBlock { Margin = new Thickness(0, 8, 0, 8) };
        status.SetBinding(TextBlock.TextProperty, new Binding("Todos.Status") { StringFormat = "Status: {0}" });
        var refresh = new Button { Content = "Refresh", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left };
        refresh.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding("Todos.RefetchCommand"));

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "DotNetQuery + WPF", FontSize = 18 });
        panel.Children.Add(status);
        panel.Children.Add(list);
        panel.Children.Add(refresh);

        var window = new Window { Title = "DotNetQuery WPF sample", Width = 420, Height = 280, Content = panel, DataContext = viewModel };
        NameScope.SetNameScope(window, new NameScope());
        window.RegisterName(list.Name, list);
        return window;
    }
}
