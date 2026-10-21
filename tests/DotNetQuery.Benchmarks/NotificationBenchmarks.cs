using BenchmarkDotNet.Attributes;
using DotNetQuery;

namespace DotNetQuery.Benchmarks;

/// <summary>
/// Measures the cost of fanning a single state change out to N subscribed observers, which is what
/// every <c>SetQueryData</c> / fetch completion pays.
/// </summary>
[MemoryDiagnoser]
public class NotificationBenchmarks
{
    private QueryClient _client = default!;
    private readonly QueryKey _key = QueryKey.Create("notify");
    private QueryObserver<int>[] _observers = default!;
    private int _value;

    [Params(1, 10, 100)]
    public int ObserverCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _client = new QueryClient();
        _observers = new QueryObserver<int>[ObserverCount];
        for (var i = 0; i < ObserverCount; i++)
        {
            var observer = _client.Watch<int>(_key);
            observer.StateChanged += OnStateChanged;
            _observers[i] = observer;
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var observer in _observers)
        {
            observer.Dispose();
        }

        _client.Dispose();
    }

    /// <summary>Writes a new value, notifying every subscribed observer synchronously.</summary>
    [Benchmark]
    public void NotifyObservers() => _client.SetQueryData(_key, _value++);

    private static void OnStateChanged(QueryState<int> state)
    {
        // No-op sink: we are measuring dispatch cost, not consumer work.
    }
}
