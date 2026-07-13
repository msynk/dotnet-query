using System.Text.Json;
using DotNetQuery;
using DotNetQuery.DevTools;
using DotNetQuery.DevTools.Cli;

// A standalone, out-of-process DevTools inspector: it reads a JSON export produced by any app's
// QueryDevTools.ExportJson() and renders a console dashboard. With no argument it generates a live demo
// export so the tool is self-contained (and doubles as an end-to-end smoke test: exit 0 == pass).
//
//   dnq-devtools export.json      inspect an export file
//   dnq-devtools -                inspect an export piped on stdin
//   dnq-devtools emit             print a demo export as JSON (pipe it back in with `-`)
//   dnq-devtools                  render a generated demo export

if (args.Length > 0 && args[0] == "emit")
{
    Console.Write(await GenerateDemoExportAsync());
    return 0;
}

var json = await GetExportJsonAsync(args);

DevToolsExport? export;
try
{
    export = JsonSerializer.Deserialize<DevToolsExport>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
catch (JsonException ex)
{
    Console.Error.WriteLine($"Could not parse the DevTools export: {ex.Message}");
    return 2;
}

if (export is null)
{
    Console.Error.WriteLine("The DevTools export was empty.");
    return 2;
}

Renderer.Render(export);
return export.Stats.TotalQueries > 0 ? 0 : 1;

static async Task<string> GetExportJsonAsync(string[] args)
{
    if (args.Length > 0 && args[0] == "-")
    {
        return await Console.In.ReadToEndAsync();
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

    Console.WriteLine("(no export given — generating a live demo export)\n");
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

    // An observed (active) query, a couple of plain fetches, a failure, a mutation, and an invalidation —
    // enough to populate every section of the dashboard.
    using var observer = client.Watch<string[]>(QueryKey.Create("users"));
    await client.FetchAsync(QueryKey.Create("users"), _ => Task.FromResult(new[] { "alice", "bob" }));
    await client.FetchAsync(QueryKey.Create("orders", 42), _ => Task.FromResult(new { Id = 42 }));
    await client.FetchAsync<int>(QueryKey.Create("flaky"), _ => throw new InvalidOperationException("upstream 500"));
    await client.MutateAsync<string, int>((s, _) => Task.FromResult(s.Length), "hello");
    await client.InvalidateQueriesAsync(QueryKey.Create("users"));

    return devtools.ExportJson();
}
