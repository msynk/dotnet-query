using System.Diagnostics;
using System.Diagnostics.Metrics;
using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class DiagnosticsTests
{
    private static QueryClient CreateClient(FakeTimeProvider time) =>
        new(new QueryClientOptions { DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero), DefaultRetryCount = 0 }, time);

    [Fact]
    public async Task Successful_fetch_raises_started_and_completed_events()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var events = new List<DotNetQueryEvent>();
        client.DiagnosticEvent += events.Add;

        await client.FetchAsync(QueryKey.Create("d"), _ => Task.FromResult(1));

        Assert.Contains(events, e => e is QueryStartedEvent);
        Assert.Contains(events, e => e is QueryCompletedEvent);
        Assert.DoesNotContain(events, e => e is QueryFailedEvent);
    }

    [Fact]
    public async Task Failed_fetch_raises_failed_event()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var events = new List<DotNetQueryEvent>();
        client.DiagnosticEvent += events.Add;

        await client.FetchAsync<int>(QueryKey.Create("f"), _ => throw new InvalidOperationException());

        var failed = Assert.IsType<QueryFailedEvent>(Assert.Single(events, e => e is QueryFailedEvent));
        Assert.IsType<InvalidOperationException>(failed.Error);
    }

    [Fact]
    public async Task Deduplicated_fetch_raises_single_started_event()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var started = 0;
        client.DiagnosticEvent += e => { if (e is QueryStartedEvent) Interlocked.Increment(ref started); };
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var t1 = client.FetchAsync(QueryKey.Create("dedup"), _ => gate.Task);
        var t2 = client.FetchAsync(QueryKey.Create("dedup"), _ => gate.Task);
        gate.SetResult(1);
        await Task.WhenAll(t1, t2);

        Assert.Equal(1, started); // only one real fetch despite two callers
    }

    [Fact]
    public async Task Invalidation_raises_event_with_keys()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        client.SetQueryData(QueryKey.Create("users", 1), 1);
        QueryInvalidatedEvent? captured = null;
        client.DiagnosticEvent += e => { if (e is QueryInvalidatedEvent inv) captured = inv; };

        await client.InvalidateQueriesAsync(QueryKey.Create("users"));

        Assert.NotNull(captured);
        Assert.Contains(QueryKey.Create("users", 1), captured!.Keys);
    }

    [Fact]
    public async Task Mutation_raises_started_and_completed_events()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var events = new List<DotNetQueryEvent>();
        client.DiagnosticEvent += events.Add;

        await client.MutateAsync<int, int>((v, _) => Task.FromResult(v), 1);

        Assert.Contains(events, e => e is MutationStartedEvent);
        var completed = Assert.IsType<MutationCompletedEvent>(Assert.Single(events, e => e is MutationCompletedEvent));
        Assert.True(completed.Succeeded);
    }

    [Fact]
    public async Task ActivitySource_produces_a_span_per_fetch()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var key = QueryKey.Create("traced", Guid.NewGuid().ToString("N"));

        // The ActivitySource is process-global, so filter to this test's unique key inside the
        // (concurrently-invoked) callback and collect into a thread-safe bag.
        var spans = new System.Collections.Concurrent.ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DotNetQueryDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                if (a.GetTagItem("dotnetquery.query.key") as string == key.ToString())
                {
                    spans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        await client.FetchAsync(key, _ => Task.FromResult(1));

        var span = Assert.Single(spans);
        Assert.Equal("dotnetquery.query.fetch", span.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
    }

    [Fact]
    public async Task Meter_records_query_counters()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        long started = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == DotNetQueryDiagnostics.MeterName &&
                    instrument.Name == "dotnetquery.queries.started")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref started, measurement));
        listener.Start();

        await client.FetchAsync(QueryKey.Create("metered"), _ => Task.FromResult(1));
        listener.Dispose();

        Assert.True(started >= 1);
    }
}
