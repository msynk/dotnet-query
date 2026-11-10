using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace DotNetQuery.E2E;

/// <summary>
/// Starts the TodoApp demo in a real Kestrel process and a headless browser. The browser is the locally installed
/// Chrome (or Edge) driven through Playwright, so no browser download is needed; when neither is available (or the
/// demo was not built) <see cref="SkipReason"/> is set and the tests are skipped rather than failed.
/// </summary>
public sealed class TodoAppFixture : IAsyncLifetime
{
    private Process? _server;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = string.Empty;

    public IBrowser? Browser { get; private set; }

    public string? SkipReason { get; private set; }

    public string ServerLog => _log.ToString();

    private readonly System.Text.StringBuilder _log = new();

    public async Task InitializeAsync()
    {
        var appDll = LocateDemo();
        if (appDll is null)
        {
            SkipReason = "The TodoApp demo has not been built for this configuration.";
            return;
        }

        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        var start = new ProcessStartInfo("dotnet", $"\"{appDll}\" --urls {BaseUrl}")
        {
            WorkingDirectory = Path.GetDirectoryName(appDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_CONTENTROOT"] = DemoDirectory();
        _server = Process.Start(start)!;
        _server.OutputDataReceived += (_, e) => { lock (_log) { _log.AppendLine(e.Data); } };
        _server.ErrorDataReceived += (_, e) => { lock (_log) { _log.AppendLine(e.Data); } };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(BaseUrl);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    break;
                }
            }
            catch (HttpRequestException)
            {
            }

            if (DateTime.UtcNow > deadline || _server.HasExited)
            {
                throw new InvalidOperationException($"TodoApp did not start.\n{ServerLog}");
            }

            await Task.Delay(250);
        }

        _playwright = await Playwright.CreateAsync();
        foreach (var channel in new[] { "chrome", "msedge" })
        {
            try
            {
                Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = channel, Headless = true });
                break;
            }
            catch (PlaywrightException)
            {
                // Not installed; try the next one.
            }
        }

        if (Browser is null)
        {
            SkipReason = "No installed Chrome or Edge browser was found for Playwright.";
        }
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_server is { HasExited: false })
        {
            _server.Kill(entireProcessTree: true);
            await _server.WaitForExitAsync();
        }

        _server?.Dispose();
    }

    private static string DemoDirectory() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "demo", "TodoApp"));

    // tests/DotNetQuery.E2E/bin/<Configuration>/<tfm>/ -> demo/TodoApp/bin/<Configuration>/<tfm>/TodoApp.dll
    private static string? LocateDemo()
    {
        var tfmDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = tfmDirectory.Parent!.Name;
        var dll = Path.Combine(DemoDirectory(), "bin", configuration, tfmDirectory.Name, "TodoApp.dll");
        return File.Exists(dll) ? dll : null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition(Name)]
public sealed class TodoAppCollection : ICollectionFixture<TodoAppFixture>
{
    public const string Name = "TodoApp";
}
