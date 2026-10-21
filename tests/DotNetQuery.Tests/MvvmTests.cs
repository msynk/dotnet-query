using System.ComponentModel;
using DotNetQuery;
using DotNetQuery.Mvvm;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

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

        // The Loading transition happens synchronously when the fetch starts.
        Assert.True(oq.IsLoading);
        Assert.True(oq.IsPending);
        gate.SetResult(7);
        await WaitUntilAsync(() => { lock (changed) { return changed.Contains(nameof(ObservableQuery<int>.HasData)); } });

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

    [Fact]
    public async Task ObservableQuery_ChangeQuery_switches_keys_and_can_keep_previous_data()
    {
        using var client = CreateClient();
        var page2 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var oq = new ObservableQuery<string>(client, new QueryDefinition<string>(QueryKey.Create("page", 1), _ => Task.FromResult("one")));
        await WaitUntilAsync(() => oq.Data == "one");

        oq.ChangeQuery(new QueryDefinition<string>(QueryKey.Create("page", 2), _ => page2.Task), keepPreviousData: true);

        Assert.Equal(QueryKey.Create("page", 2), oq.Key);
        Assert.Equal("one", oq.Data);
        Assert.True(oq.IsPlaceholderData);
        Assert.False(oq.IsPending);

        page2.SetResult("two");
        await WaitUntilAsync(() => oq.Data == "two");
        Assert.False(oq.IsPlaceholderData);
        Assert.Equal(0, client.Cache.Describe(QueryKey.Create("page", 1))!.ObserverCount);
    }

    [Fact]
    public async Task ObservableQuery_with_ThrowOnError_does_not_leak_unobserved_exceptions()
    {
        using var client = CreateClient();
        using var oq = new ObservableQuery<int>(
            client,
            QueryKey.Create("throwing"),
            _ => Task.FromException<int>(new InvalidOperationException("bad")),
            new QueryOptions { ThrowOnError = true });

        await WaitUntilAsync(() => oq.IsError);
        Assert.Equal("bad", oq.Error!.Message);
        Assert.True(oq.IsLoadingError);
    }
}
