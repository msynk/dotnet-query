using DotNetQuery;
using DotNetQuery.DevTools;
using DotNetQuery.Synchronization;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotNetQuery.Tests;

public sealed class QuerySyncDiagnosticsTests
{
    private sealed record Secret(int Id, string Password);

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, int Id, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task Synchronization_is_logged_without_payloads()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var logger = new ListLogger();
        using var logging = client.LogDiagnostics(logger);
        await using var sync = new QueryEventSynchronizer(client, options: new QueryEventSyncOptions { Name = "hub" });
        sync.Map<Secret>("Changed", m => m
            .WithMetadata(s => new QueryEventMetadata { EventId = $"e{s.Id}" })
            .Update<int>(QueryKey.Create("n"), (_, _) => throw new InvalidOperationException("bad")));
        client.SetQueryData(QueryKey.Create("n"), 0);

        await sync.ProcessAsync("Changed", new Secret(1, "hunter2"));
        await sync.ResynchronizeAsync();

        lock (logger.Entries)
        {
            var failed = Assert.Single(logger.Entries, e => e.Id == 1200);
            Assert.Equal(LogLevel.Warning, failed.Level);
            Assert.Contains("Changed", failed.Message);
            Assert.Contains("e1", failed.Message);
            Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("hunter2"));
            Assert.Contains(logger.Entries, e => e.Id == 1201 && e.Message.Contains("Synchronizing"));
            Assert.Contains(logger.Entries, e => e.Id == 1202 && e.Level == LogLevel.Information);
        }
    }

    [Fact]
    public async Task DevTools_inspect_attached_synchronizers_and_export_them()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var source = new InMemoryQueryEventSource();
        await using var sync = new QueryEventSynchronizer(client, source, new QueryEventSyncOptions { Name = "todos-hub" });
        sync.Map<int>("TodoDeleted", m => m.Invalidate(QueryKey.Create("todos")));
        using var devtools = new QueryDevTools(client);
        var changes = 0;
        devtools.Changed += () => Interlocked.Increment(ref changes);

        devtools.AttachSynchronizer(sync);
        devtools.AttachSynchronizer(sync); // idempotent
        await sync.StartAsync();
        await TestUtil.WaitUntilAsync(() => sync.Status == QuerySyncStatus.Synchronized);
        await sync.ProcessAsync("TodoDeleted", 1);

        var stats = Assert.Single(devtools.Synchronizers);
        Assert.Equal("todos-hub", stats.Name);
        Assert.Equal(QuerySyncStatus.Synchronized, stats.Status);
        Assert.Equal(1, stats.Mappings["TodoDeleted"]);
        Assert.Equal(1, stats.Applied);
        Assert.NotNull(stats.LastSynchronizedAt);
        Assert.True(Volatile.Read(ref changes) > 0);

#pragma warning disable IL2026, IL3050 // DevTools export uses reflection-based JSON (tests are not trimmed).
        var json = devtools.ExportJson();
#pragma warning restore IL2026, IL3050
        Assert.Contains("\"todos-hub\"", json);
        Assert.Contains("\"synchronized\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(devtools.RecentEvents(), e => e is QuerySyncEventProcessedEvent { EventName: "TodoDeleted" });
    }
}
