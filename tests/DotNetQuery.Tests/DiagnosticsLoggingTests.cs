using DotNetQuery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotNetQuery.Tests;

public class DiagnosticsLoggingTests
{
    private sealed class ListLogger(List<(LogLevel Level, EventId Id, string Message, Exception? Error, string Category)>? entries = null, string category = "")
        : ILogger, ILoggerFactory
    {
        public List<(LogLevel Level, EventId Id, string Message, Exception? Error, string Category)> Entries { get; } = entries ?? [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, eventId, formatter(state, exception), exception, category));
            }
        }

        public ILogger CreateLogger(string categoryName) => new ListLogger(Entries, categoryName);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Fetches_failures_and_mutations_are_logged_without_variables()
    {
        using var client = new QueryClient(TestUtil.NoRetry(o => o.DiagnosticKeyFormatter = k => $"{k[0]}/***"));
        var logger = new ListLogger();
        using (client.LogDiagnostics(logger))
        {
            await client.FetchAsync(QueryKey.Create("users", 42), _ => Task.FromResult(1));
            await client.FetchAsync(QueryKey.Create("users", 43), _ => Task.FromException<int>(new InvalidOperationException("boom")));
            await client.MutateAsync<string, int>((_, _) => throw new InvalidOperationException("nope"), "secret-password",
                new MutationOptions<string, int> { MutationKey = QueryKey.Create("users", "add"), ThrowOnError = false });
        }

        await client.FetchAsync(QueryKey.Create("after"), _ => Task.FromResult(1)); // disposed: not logged

        lock (logger.Entries)
        {
            Assert.Contains(logger.Entries, e => e.Id.Id == 1001 && e.Level == LogLevel.Debug && e.Message.Contains("users/***"));
            var failed = Assert.Single(logger.Entries, e => e.Id.Id == 1002);
            Assert.Equal(LogLevel.Warning, failed.Level);
            Assert.Equal("boom", failed.Error!.Message);
            var mutationFailed = Assert.Single(logger.Entries, e => e.Id.Id == 1102);
            Assert.Equal(LogLevel.Warning, mutationFailed.Level);
            Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret-password") || e.Message.Contains("43]") || e.Message.Contains("\"after\""));
        }
    }

    [Fact]
    public async Task AddDiagnosticLogging_attaches_the_registered_logger_factory()
    {
        var factory = new ListLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(factory);
        services.AddDotNetQuery(o => o.DefaultRetryCount = 0)
            .AddDiagnosticLogging(new QueryDiagnosticsLoggingOptions { RoutineLevel = LogLevel.Information })
            .AddDiagnosticLogging(); // idempotent
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IQueryClient>();

        await client.FetchAsync(QueryKey.Create("todos"), _ => Task.FromResult(1));

        lock (factory.Entries)
        {
            Assert.Equal(2, factory.Entries.Count(e => e.Id.Id is 1000 or 1001));
            Assert.All(factory.Entries, e => Assert.Equal(QueryDiagnosticsLogging.CategoryName, e.Category));
            Assert.All(factory.Entries, e => Assert.Equal(LogLevel.Information, e.Level));
        }
    }
}
