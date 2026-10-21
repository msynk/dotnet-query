using System.Diagnostics;

namespace DotNetQuery.Tests;

internal static class TestUtil
{
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not met within the timeout.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>A fetcher that signals when it starts and then waits for <paramref name="release"/> (or cancellation).</summary>
    public static Func<CancellationToken, Task<T>> Gated<T>(TaskCompletionSource started, Task<T> release) =>
        async ct =>
        {
            started.TrySetResult();
            return await release.WaitAsync(ct);
        };

    public static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static QueryClientOptions NoRetry(Action<QueryClientOptions>? configure = null)
    {
        var options = new QueryClientOptions
        {
            DefaultRetryCount = 0,
            DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero),
            GarbageCollectionInterval = Timeout.InfiniteTimeSpan,
        };
        configure?.Invoke(options);
        return options;
    }
}
