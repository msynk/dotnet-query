using System.ComponentModel;
using DotNetQuery;
using DotNetQuery.Mvvm;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class MvvmTests
{
    private static QueryClient CreateClient() =>
        new(new QueryClientOptions { DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero), DefaultRetryCount = 0 }, new FakeTimeProvider());

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ObservableQuery_exposes_data_and_raises_property_changed()
    {
        using var client = CreateClient();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var oq = new ObservableQuery<int>(client, QueryKey.Create("mvvm"), _ => gate.Task);

        var changed = new List<string>();
        ((INotifyPropertyChanged)oq).PropertyChanged += (_, e) => { lock (changed) { changed.Add(e.PropertyName!); } };

        // A brand-new query is Idle→Loading (the loading transition is deferred one scheduler hop), so
        // IsLoading is racy here; IsPending covers both and is deterministically true from construction.
        Assert.True(oq.IsPending);
        gate.SetResult(7);
        await WaitUntilAsync(() => oq.Status == QueryStatus.Success);

        Assert.Equal(7, oq.Data);
        Assert.True(oq.IsSuccess);
        lock (changed)
        {
            Assert.Contains(nameof(ObservableQuery<int>.Data), changed);
            Assert.Contains(nameof(ObservableQuery<int>.Status), changed);
        }
    }

    [Fact]
    public async Task ObservableQuery_reflects_error()
    {
        using var client = CreateClient();
        using var oq = new ObservableQuery<int>(client, QueryKey.Create("mvvm-err"), _ => throw new InvalidOperationException("x"));

        await WaitUntilAsync(() => oq.Status == QueryStatus.Error);

        Assert.True(oq.IsError);
        Assert.IsType<InvalidOperationException>(oq.Error);
    }

    [Fact]
    public async Task ObservableMutation_tracks_pending_and_success()
    {
        using var client = CreateClient();
        var om = new ObservableMutation<int, string>(client, (v, _) => Task.FromResult($"v{v}"));
        var changed = new List<string>();
        ((INotifyPropertyChanged)om).PropertyChanged += (_, e) => { lock (changed) { changed.Add(e.PropertyName!); } };

        var result = await om.MutateAsync(3);

        Assert.True(result.IsSuccess);
        Assert.Equal("v3", om.Data);
        Assert.Equal(MutationStatus.Success, om.Status);
        lock (changed)
        {
            Assert.Contains(nameof(ObservableMutation<int, string>.IsPending), changed);
            Assert.Contains(nameof(ObservableMutation<int, string>.Status), changed);
        }
    }
}
