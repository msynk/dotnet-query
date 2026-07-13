using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

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

        var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromSeconds(5) });
        var service = new TodoService();
        var viewModel = new MainWindowViewModel(client, service);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        // Pump the dispatcher until the query resolves (its fetch runs on a real timer).
        PumpUntil(() => viewModel.Todos.HasData, TimeSpan.FromSeconds(5));

        var afterLoad = RenderedTexts(window);
        Console.WriteLine($"[verify] status={viewModel.Todos.Status}, rows=[{string.Join(" | ", viewModel.Todos.Data ?? [])}]");

        // Trigger a refetch and confirm the data changes (fetch #2).
        _ = viewModel.RefetchAsync();
        PumpUntil(() => viewModel.Todos.Data?.Any(r => r.Contains("#2")) == true, TimeSpan.FromSeconds(5));
        Console.WriteLine($"[verify] after refetch rows=[{string.Join(" | ", viewModel.Todos.Data ?? [])}]");

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

        // The gate: the query reached Success, rendered "fetch #1", and refetched to "fetch #2".
        var ok = viewModel.Todos.IsSuccess
            && afterLoad.Any(t => t.Contains("fetch #1"))
            && (viewModel.Todos.Data?.Any(r => r.Contains("#2")) == true);
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
