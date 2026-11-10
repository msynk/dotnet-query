using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotNetQuery.Avalonia;

namespace DotNetQuery.Sample.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless self-check: render the window without a display and confirm the query data appears.
        if (args.Contains("verify"))
        {
            return HeadlessVerify();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int HeadlessVerify()
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        // Stale immediately, so returning to the window refetches (the lifecycle check below).
        var focus = new FocusManager();
        using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.Zero }, focusManager: focus);
        var service = new TodoService();
        var viewModel = new MainWindowViewModel(client, service);
        using var todos = viewModel.Todos;
        var window = new MainWindow { DataContext = viewModel };
        using var lifecycle = new AvaloniaQueryLifecycle(window, new AppLifecycleSignals(focus), monitorNetwork: false);
        window.Show();

        // Pump the dispatcher until the query resolves (its fetch runs on a real timer).
        PumpUntil(() => viewModel.Todos.HasData, TimeSpan.FromSeconds(5));

        var afterLoad = RenderedTexts(window);
        Console.WriteLine($"[verify] status={viewModel.Todos.Status}, rows=[{string.Join(" | ", viewModel.Todos.Data ?? [])}]");

        // Trigger a refetch and confirm the data changes (fetch #2).
        _ = viewModel.RefetchAsync();
        PumpUntil(() => viewModel.Todos.Data?.Any(r => r.Contains("#2")) == true, TimeSpan.FromSeconds(5));
        Console.WriteLine($"[verify] after refetch rows=[{string.Join(" | ", viewModel.Todos.Data ?? [])}]");

        // The bound command refetches (fetch #3).
        todos.RefetchCommand.Execute(null);
        PumpUntil(() => viewModel.Todos.Data?.Any(r => r.Contains("#3")) == true, TimeSpan.FromSeconds(5));
        var commandRefetched = viewModel.Todos.Data?.Any(r => r.Contains("#3")) == true;
        Console.WriteLine($"[verify] after RefetchCommand rows=[{string.Join(" | ", viewModel.Todos.Data ?? [])}]");

        // Lifecycle: the window loses the foreground (hidden - the headless platform's way to deactivate a window)
        // and is activated again; the stale query refetches through AvaloniaQueryLifecycle -> AppLifecycleSignals ->
        // FocusManager (fetch #4).
        window.Hide();
        Dispatcher.UIThread.RunJobs();
        window.Show();
        window.Activate();
        PumpUntil(() => viewModel.Todos.Data?.Any(r => r.Contains("#4")) == true, TimeSpan.FromSeconds(5));
        var focusRefetched = viewModel.Todos.Data?.Any(r => r.Contains("#4")) == true;
        Console.WriteLine($"[verify] after re-activation rows=[{string.Join(" | ", viewModel.Todos.Data ?? [])}]");

        Console.WriteLine($"[verify] rendered visual tree text: [{string.Join(" | ", afterLoad)}]");

        // A real rendered frame is a nice artifact but not the gate - capture is best-effort.
        try
        {
            var outPath = Path.Combine(AppContext.BaseDirectory, "avalonia-verify.png");
            window.CaptureRenderedFrame()?.Save(outPath);
            Console.WriteLine($"[verify] rendered frame saved: {outPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[verify] frame capture skipped: {ex.GetType().Name}");
        }

        // The gate: the query reached Success, rendered "fetch #1", refetched on invalidation, through the bound
        // command, and when the window was activated again.
        var ok = viewModel.Todos.IsSuccess
            && afterLoad.Any(t => t.Contains("fetch #1"))
            && commandRefetched
            && focusRefetched;
        Console.WriteLine(ok ? "[verify] PASS" : "[verify] FAIL");
        return ok ? 0 : 1;
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> RenderedTexts(Visual root) =>
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToList();
}
