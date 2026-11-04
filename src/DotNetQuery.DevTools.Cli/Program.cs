using System.Text.Json;
using DotNetQuery;
using DotNetQuery.DevTools;
using DotNetQuery.DevTools.Cli;

// A standalone, out-of-process DevTools inspector: it reads a JSON export produced by any app's
// QueryDevTools.ExportJson() and renders a console dashboard. With no argument it generates a live demo
// export so the tool is self-contained (and doubles as an end-to-end smoke test: exit 0 == pass).
//
//   dnq-devtools export.json                  inspect an export file
//   dnq-devtools -                            inspect an export piped on stdin
//   dnq-devtools http://127.0.0.1:9230/       inspect a running app once (QueryDevToolsServer)
//   dnq-devtools watch http://127.0.0.1:9230/ [--interval 1000] [--count N]
//                                             live dashboard of a running app, refreshed until Ctrl+C
//   dnq-devtools watch demo [--count N]       watch an in-process demo app (also the CI smoke test of watch mode)
//   dnq-devtools emit                         print a demo export as JSON (pipe it back in with `-`)
//   dnq-devtools                              render a generated demo export

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

if (args.Length > 0 && args[0] == "emit")
{
    Console.Write(await GenerateDemoExportAsync());
    return 0;
}

if (args.Length > 0 && args[0] == "watch")
{
    return await WatchAsync(args.Skip(1).ToArray(), jsonOptions);
}

var json = await GetExportJsonAsync(args);
var export = Parse(json, jsonOptions);
if (export is null)
{
    return 2;
}

Renderer.Render(export);
return export.Stats.TotalQueries > 0 ? 0 : 1;

static DevToolsExport? Parse(string json, JsonSerializerOptions options)
{
    try
    {
        var export = JsonSerializer.Deserialize<DevToolsExport>(json, options);
        if (export is null)
        {
            Console.Error.WriteLine("The DevTools export was empty.");
        }

        return export;
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"Could not parse the DevTools export: {ex.Message}");
        return null;
    }
}

static async Task<int> WatchAsync(string[] args, JsonSerializerOptions options)
{
    if (args.Length > 0 && args[0] == "demo")
    {
        using var client = new QueryClient(new QueryClientOptions { DefaultRetryCount = 0 });
        using var devtools = new QueryDevTools(client);
        await using var server = QueryDevToolsServer.Start(devtools);
        using var observer = client.Watch<string[]>(QueryKey.Create("users"));
        await client.FetchAsync(QueryKey.Create("users"), _ => Task.FromResult(new[] { "alice", "bob" }));
        await client.FetchAsync<int>(QueryKey.Create("flaky"), _ => throw new InvalidOperationException("upstream 500"));
        var rest = args.Skip(1).ToList();
        if (!rest.Contains("--count"))
        {
            rest.AddRange(["--count", "1"]);
        }

        return await WatchAsync([server.Address.ToString(), .. rest], options);
    }

    if (args.Length == 0 || !Uri.TryCreate(args[0], UriKind.Absolute, out var address))
    {
        Console.Error.WriteLine("Usage: dnq-devtools watch <http://127.0.0.1:PORT/[?token=...]> [--interval ms] [--count n]");
        return 2;
    }

    var interval = TimeSpan.FromMilliseconds(ReadOption(args, "--interval") ?? 1000);
    var remaining = ReadOption(args, "--count");
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var rendered = 0;
    while (!cancel.IsCancellationRequested && (remaining is null || rendered < remaining))
    {
        string json;
        try
        {
            json = await http.GetStringAsync(address, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            break;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"{DateTime.Now:T}  cannot reach {address}: {ex.Message}");
            await DelayAsync(interval, cancel.Token);
            continue;
        }

        if (Parse(json, options) is not { } export)
        {
            return 2;
        }

        if (!Console.IsOutputRedirected)
        {
            Console.Clear();
        }

        Console.WriteLine($"{address}  -  {DateTime.Now:T}  (Ctrl+C to stop)\n");
        Renderer.Render(export);
        rendered++;
        await DelayAsync(interval, cancel.Token);
    }

    return rendered > 0 ? 0 : 1;
}

static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(delay, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        // Stopping.
    }
}

static int? ReadOption(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value) && value >= 0 ? value : null;
}

static async Task<string> GetExportJsonAsync(string[] args)
{
    if (args.Length > 0 && args[0] == "-")
    {
        return await Console.In.ReadToEndAsync();
    }

    if (args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        return await http.GetStringAsync(url);
    }

    if (args.Length > 0)
    {
        if (!File.Exists(args[0]))
        {
            Console.Error.WriteLine($"File not found: {args[0]}");
            Environment.Exit(2);
        }

        return await File.ReadAllTextAsync(args[0]);
    }

    Console.WriteLine("(no export given - generating a live demo export)\n");
    return await GenerateDemoExportAsync();
}

static async Task<string> GenerateDemoExportAsync()
{
    using var client = new QueryClient(new QueryClientOptions
    {
        DefaultRetryCount = 0,
        DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
    });
    using var devtools = new QueryDevTools(client);

    // An observed (active) query, a couple of plain fetches, a failure, a mutation, and an invalidation -
    // enough to populate every section of the dashboard.
    using var observer = client.Watch<string[]>(QueryKey.Create("users"));
    await client.FetchAsync(QueryKey.Create("users"), _ => Task.FromResult(new[] { "alice", "bob" }));
    await client.FetchAsync(QueryKey.Create("orders", 42), _ => Task.FromResult(new { Id = 42 }));
    await client.FetchAsync<int>(QueryKey.Create("flaky"), _ => throw new InvalidOperationException("upstream 500"));
    await client.MutateAsync<string, int>(
        (s, _) => Task.FromResult(s.Length),
        "hello",
        new MutationOptions<string, int> { MutationKey = QueryKey.Create("greetings", "measure") });
    await client.InvalidateQueriesAsync(QueryKey.Create("users"));

    return devtools.ExportJson();
}
