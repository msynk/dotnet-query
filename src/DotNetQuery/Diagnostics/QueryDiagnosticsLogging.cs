using DotNetQuery;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetQuery
{
    /// <summary>Options for <see cref="QueryDiagnosticsLogging.LogDiagnostics"/>.</summary>
    public sealed record QueryDiagnosticsLoggingOptions
    {
        /// <summary>
        /// The level of routine events (fetch started/completed/cancelled, invalidations, mutation started/succeeded,
        /// optimistic writes). Defaults to <see cref="LogLevel.Debug"/>.
        /// </summary>
        public LogLevel RoutineLevel { get; init; } = LogLevel.Debug;

        /// <summary>The level of retries. Defaults to <see cref="LogLevel.Information"/>.</summary>
        public LogLevel RetryLevel { get; init; } = LogLevel.Information;

        /// <summary>The level of failed fetches and mutations. Defaults to <see cref="LogLevel.Warning"/>.</summary>
        public LogLevel FailureLevel { get; init; } = LogLevel.Warning;

        /// <summary>
        /// Formats query and mutation keys for the log. Defaults to the client's
        /// <see cref="QueryClientOptions.DiagnosticKeyFormatter"/> (when the client is a <see cref="QueryClient"/>), else
        /// <see cref="QueryKey.ToString"/> - use it to keep personal data in keys out of logs.
        /// </summary>
        public Func<QueryKey, string>? KeyFormatter { get; init; }
    }

    /// <summary>
    /// Writes a client's <see cref="IQueryClient.DiagnosticEvent"/> stream to an <see cref="ILogger"/>: failures as
    /// warnings (with the exception), subscriber errors as errors, retries as information and everything else at debug
    /// level. Mutation variables and cached data are never logged.
    /// </summary>
    public static partial class QueryDiagnosticsLogging
    {
        /// <summary>The logger category used by <c>AddDiagnosticLogging</c>.</summary>
        public const string CategoryName = "DotNetQuery";

        /// <summary>
        /// Starts logging <paramref name="client"/>'s diagnostic events to <paramref name="logger"/>. Dispose the result to
        /// stop.
        /// </summary>
        public static IDisposable LogDiagnostics(this IQueryClient client, ILogger logger, QueryDiagnosticsLoggingOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(logger);
            return new Subscription(client, logger, options ?? new QueryDiagnosticsLoggingOptions());
        }

        private sealed class Subscription : IDisposable
        {
            private readonly IQueryClient _client;
            private readonly ILogger _logger;
            private readonly QueryDiagnosticsLoggingOptions _options;
            private readonly Func<QueryKey, string>? _format;
            private int _disposed;

            public Subscription(IQueryClient client, ILogger logger, QueryDiagnosticsLoggingOptions options)
            {
                _client = client;
                _logger = logger;
                _options = options;
                _format = options.KeyFormatter ?? (client as QueryClient)?.Options.DiagnosticKeyFormatter;
                client.DiagnosticEvent += OnEvent;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _client.DiagnosticEvent -= OnEvent;
                }
            }

            private string Format(QueryKey? key)
            {
                if (key is null)
                {
                    return "(none)";
                }

                if (_format is null)
                {
                    return key.ToString();
                }

                try
                {
                    return _format(key);
                }
                catch (Exception)
                {
                    return "(unformattable key)";
                }
            }

            private void OnEvent(DotNetQueryEvent e)
            {
                var routine = _options.RoutineLevel;
                switch (e)
                {
                    case QueryStartedEvent s when _logger.IsEnabled(routine):
                        QueryStarted(_logger, routine, Format(s.Key));
                        break;
                    case QueryCompletedEvent c when _logger.IsEnabled(routine):
                        QueryCompleted(_logger, routine, Format(c.Key), c.Duration.TotalMilliseconds);
                        break;
                    case QueryFailedEvent f when _logger.IsEnabled(_options.FailureLevel):
                        QueryFailed(_logger, _options.FailureLevel, f.Error, Format(f.Key), f.FailureCount, f.Duration.TotalMilliseconds);
                        break;
                    case QueryCancelledEvent x when _logger.IsEnabled(routine):
                        QueryCancelled(_logger, routine, Format(x.Key), x.Duration.TotalMilliseconds);
                        break;
                    case QueryRetryEvent r when _logger.IsEnabled(_options.RetryLevel):
                        QueryRetrying(_logger, _options.RetryLevel, r.Error, Format(r.Key), r.FailureCount);
                        break;
                    case QueryInvalidatedEvent i when _logger.IsEnabled(routine):
                        QueriesInvalidated(_logger, routine, i.Keys.Count, string.Join(", ", i.Keys.Take(10).Select(k => Format(k))));
                        break;
                    case QueryObserverErrorEvent o when _logger.IsEnabled(LogLevel.Error):
                        ObserverFailed(_logger, o.Error, Format(o.Key));
                        break;
                    case MutationStartedEvent m when _logger.IsEnabled(routine):
                        MutationStarted(_logger, routine, m.MutationId, Format(m.MutationKey));
                        break;
                    case MutationCompletedEvent { Succeeded: true } m when _logger.IsEnabled(routine):
                        MutationSucceeded(_logger, routine, m.MutationId, Format(m.MutationKey), m.Duration.TotalMilliseconds);
                        break;
                    case MutationCompletedEvent { Succeeded: false } m when _logger.IsEnabled(_options.FailureLevel):
                        MutationFailed(_logger, _options.FailureLevel, m.Error, m.MutationId, Format(m.MutationKey), m.FailureCount, m.Duration.TotalMilliseconds);
                        break;
                    case OptimisticUpdateAppliedEvent a when _logger.IsEnabled(routine):
                        OptimisticApplied(_logger, routine, a.MutationId, a.Keys.Count);
                        break;
                    case OptimisticUpdateRolledBackEvent b when _logger.IsEnabled(routine):
                        OptimisticRolledBack(_logger, routine, b.MutationId, b.Restored.Count, b.Refetched.Count);
                        break;
                }
            }
        }

        [LoggerMessage(EventId = 1000, EventName = "QueryStarted", Message = "Fetching query {Key}")]
        private static partial void QueryStarted(ILogger logger, LogLevel level, string key);

        [LoggerMessage(EventId = 1001, EventName = "QueryCompleted", Message = "Query {Key} fetched in {DurationMs:0.0} ms")]
        private static partial void QueryCompleted(ILogger logger, LogLevel level, string key, double durationMs);

        [LoggerMessage(EventId = 1002, EventName = "QueryFailed", Message = "Query {Key} failed after {FailureCount} attempt(s) in {DurationMs:0.0} ms")]
        private static partial void QueryFailed(ILogger logger, LogLevel level, Exception error, string key, int failureCount, double durationMs);

        [LoggerMessage(EventId = 1003, EventName = "QueryCancelled", Message = "Query {Key} fetch cancelled after {DurationMs:0.0} ms")]
        private static partial void QueryCancelled(ILogger logger, LogLevel level, string key, double durationMs);

        [LoggerMessage(EventId = 1004, EventName = "QueryRetrying", Message = "Retrying query {Key} after failure {FailureCount}")]
        private static partial void QueryRetrying(ILogger logger, LogLevel level, Exception error, string key, int failureCount);

        [LoggerMessage(EventId = 1005, EventName = "QueriesInvalidated", Message = "Invalidated {Count} key(s): {Keys}")]
        private static partial void QueriesInvalidated(ILogger logger, LogLevel level, int count, string keys);

        [LoggerMessage(EventId = 1006, EventName = "QueryObserverFailed", Level = LogLevel.Error, Message = "A subscriber of query {Key} threw")]
        private static partial void ObserverFailed(ILogger logger, Exception error, string key);

        [LoggerMessage(EventId = 1100, EventName = "MutationStarted", Message = "Mutation #{MutationId} {MutationKey} started")]
        private static partial void MutationStarted(ILogger logger, LogLevel level, long mutationId, string mutationKey);

        [LoggerMessage(EventId = 1101, EventName = "MutationSucceeded", Message = "Mutation #{MutationId} {MutationKey} succeeded in {DurationMs:0.0} ms")]
        private static partial void MutationSucceeded(ILogger logger, LogLevel level, long mutationId, string mutationKey, double durationMs);

        [LoggerMessage(EventId = 1102, EventName = "MutationFailed", Message = "Mutation #{MutationId} {MutationKey} failed after {FailureCount} attempt(s) in {DurationMs:0.0} ms")]
        private static partial void MutationFailed(ILogger logger, LogLevel level, Exception? error, long mutationId, string mutationKey, int failureCount, double durationMs);

        [LoggerMessage(EventId = 1103, EventName = "OptimisticUpdateApplied", Message = "Mutation #{MutationId} wrote {Count} key(s) optimistically")]
        private static partial void OptimisticApplied(ILogger logger, LogLevel level, long mutationId, int count);

        [LoggerMessage(EventId = 1104, EventName = "OptimisticUpdateRolledBack", Message = "Mutation #{MutationId} rolled back {Restored} key(s), refetching {Refetched}")]
        private static partial void OptimisticRolledBack(ILogger logger, LogLevel level, long mutationId, int restored, int refetched);
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Logging registration for <see cref="IDotNetQueryBuilder"/>.</summary>
    public static class DotNetQueryLoggingBuilderExtensions
    {
        /// <summary>
        /// Logs every client's diagnostic events through the registered <see cref="ILoggerFactory"/> (category
        /// <see cref="QueryDiagnosticsLogging.CategoryName"/>) - see <see cref="QueryDiagnosticsLogging.LogDiagnostics"/>.
        /// Without a registered logger factory nothing is logged.
        /// </summary>
        public static IDotNetQueryBuilder AddDiagnosticLogging(this IDotNetQueryBuilder builder, QueryDiagnosticsLoggingOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            options ??= new QueryDiagnosticsLoggingOptions();
            var services = builder.Services;
            var existing = services.LastOrDefault(d => d.ServiceType == typeof(IQueryClient) && !d.IsKeyedService);
            if (existing is null || services.Any(d => d.ServiceType == typeof(LoggingMarker)))
            {
                return builder;
            }

            services.TryAddSingleton(options);
            services.AddSingleton<LoggingMarker>();
            services.Remove(existing);
            services.Add(ServiceDescriptor.Describe(typeof(IQueryClient), sp =>
            {
                var client = (IQueryClient)Create(existing, sp);
                var factory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
                client.LogDiagnostics(factory.CreateLogger(QueryDiagnosticsLogging.CategoryName), sp.GetRequiredService<QueryDiagnosticsLoggingOptions>());
                return client;
            }, existing.Lifetime));
            return builder;
        }

        private static object Create(ServiceDescriptor descriptor, IServiceProvider services) =>
            descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(services)
            ?? throw new InvalidOperationException("AddDiagnosticLogging requires the IQueryClient registered by AddDotNetQuery.");

        private sealed class LoggingMarker;
    }
}
