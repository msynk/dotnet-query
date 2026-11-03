using System.ComponentModel;
using System.Net.NetworkInformation;
using DotNetQuery;
using DotNetQuery.Mvvm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class LifecycleAndMvvmTests
{
    [Fact]
    public void Activation_signals_focus_only_after_a_deactivation()
    {
        var focus = new FocusManager();
        var signals = new AppLifecycleSignals(focus);
        var raised = 0;
        focus.Raised += () => raised++;

        signals.NotifyActivated(); // startup activation: the app was never away
        Assert.Equal(0, raised);

        signals.NotifyDeactivated();
        Assert.False(signals.IsActive);
        signals.NotifyActivated();
        signals.NotifyActivated(); // switching between the app's own windows
        Assert.Equal(1, raised);
        Assert.True(signals.IsActive);
    }

    [Fact]
    public void Resume_signals_resume_and_suppresses_the_activation_that_follows()
    {
        var focus = new FocusManager();
        var resume = new ResumeManager();
        var signals = new AppLifecycleSignals(focus, resume);
        var (focused, resumed) = (0, 0);
        focus.Raised += () => focused++;
        resume.Raised += () => resumed++;

        signals.NotifyDeactivated();
        signals.NotifyResumed();
        signals.NotifyActivated(); // mobile platforms raise both on return; one refetch is enough

        Assert.Equal((0, 1), (focused, resumed));
    }

    [Fact]
    public void Network_availability_drives_the_connectivity_provider()
    {
        var connectivity = new ConnectivityProvider();
        var signals = new AppLifecycleSignals(connectivity: connectivity);

        signals.NotifyNetworkAvailability(false);
        Assert.False(connectivity.IsOnline);
        signals.NotifyNetworkAvailability(true);
        Assert.True(connectivity.IsOnline);
        Assert.Same(connectivity, signals.Connectivity);
    }

    [Fact]
    public async Task Returning_to_the_app_refetches_stale_active_queries()
    {
        var time = new FakeTimeProvider();
        var focus = new FocusManager();
        using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromSeconds(30) }, time, focus);
        var signals = new AppLifecycleSignals(focus);
        var key = QueryKey.Create("lifecycle");
        var calls = 0;
        using var observer = client.Watch<int>(key);
        await client.FetchAsync(key, _ => Task.FromResult(Interlocked.Increment(ref calls)));
        time.Advance(TimeSpan.FromMinutes(1));

        signals.NotifyActivated(); // never left: nothing happens
        await Task.Delay(50);
        Assert.Equal(1, calls);

        signals.NotifyDeactivated();
        signals.NotifyActivated();
        await TestUtil.WaitUntilAsync(() => Volatile.Read(ref calls) == 2);
    }

    [Fact]
    public void Network_monitor_seeds_the_current_availability_and_stops_on_dispose()
    {
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(!NetworkInterface.GetIsNetworkAvailable());

        using (new NetworkConnectivityMonitor(connectivity))
        {
            Assert.Equal(NetworkInterface.GetIsNetworkAvailable(), connectivity.IsOnline);
        }

        Assert.Throws<ArgumentNullException>(() => new NetworkConnectivityMonitor(null!));
    }

    [Fact]
    public void AddDotNetQuery_registers_lifecycle_signals_wired_to_the_clients_managers()
    {
        using var provider = new ServiceCollection().AddDotNetQuery().Services.BuildServiceProvider();
        var signals = provider.GetRequiredService<AppLifecycleSignals>();
        var raised = 0;
        provider.GetRequiredService<FocusManager>().Raised += () => raised++;

        signals.NotifyDeactivated();
        signals.NotifyActivated();

        Assert.Equal(1, raised);
        Assert.Same(provider.GetRequiredService<ConnectivityProvider>(), signals.Connectivity);
    }

    [Fact]
    public async Task ObservableQuery_RefetchCommand_refetches()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var calls = 0;
        using var query = new ObservableQuery<int>(client, QueryKey.Create("cmd"), _ => Task.FromResult(Interlocked.Increment(ref calls)));
        await TestUtil.WaitUntilAsync(() => query.Data == 1);

        Assert.True(query.RefetchCommand.CanExecute(null));
        query.RefetchCommand.Execute(null);

        await TestUtil.WaitUntilAsync(() => query.Data == 2);
    }

    [Fact]
    public async Task ObservableMutation_MutateCommand_runs_once_at_a_time_and_validates_its_parameter()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var release = TestUtil.NewSignal();
        var runs = 0;
        using var mutation = new ObservableMutation<int, int>(client, async (value, _) =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            return value * 2;
        });
        var canExecuteChanges = 0;
        mutation.MutateCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        Assert.False(mutation.MutateCommand.CanExecute("not an int"));
        Assert.False(mutation.MutateCommand.CanExecute(null)); // int is not nullable
        Assert.True(mutation.MutateCommand.CanExecute(21));
        Assert.False(mutation.ResetCommand.CanExecute(null)); // idle

        mutation.MutateCommand.Execute(21);
        await TestUtil.WaitUntilAsync(() => mutation.IsPending);
        Assert.False(mutation.MutateCommand.CanExecute(21)); // a double-click does not submit twice
        mutation.MutateCommand.Execute(21);

        release.SetResult();
        await TestUtil.WaitUntilAsync(() => mutation.IsSuccess);
        Assert.Equal(42, mutation.Data);
        Assert.Equal(1, Volatile.Read(ref runs));
        Assert.True(canExecuteChanges > 0);

        Assert.True(mutation.ResetCommand.CanExecute(null));
        mutation.ResetCommand.Execute(null);
        await TestUtil.WaitUntilAsync(() => mutation.IsIdle);
    }

    [Fact]
    public async Task ObservableMutation_MutateCommand_accepts_null_for_reference_variables_and_observes_failures()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var mutation = new ObservableMutation<string?, int>(
            client,
            (_, _) => throw new InvalidOperationException("rejected"),
            new MutationOptions<string?, int> { ThrowOnError = true });

        Assert.True(mutation.MutateCommand.CanExecute(null));
        mutation.MutateCommand.Execute(null); // the failure must not escape to the UI thread

        await TestUtil.WaitUntilAsync(() => mutation.IsError);
        Assert.Equal("rejected", mutation.Error!.Message);
    }

    private static readonly InfiniteQueryOptions<int, int[]> PageOptions = new()
    {
        InitialPageParam = 0,
        GetNextPageParam = (_, _, parameters) => parameters[^1] < 2 ? NextPageParam.Of(parameters[^1] + 1) : NextPageParam.None<int>(),
    };

    [Fact]
    public async Task ObservableInfiniteQuery_loads_pages_through_its_command_until_the_last_one()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        using var feed = new ObservableInfiniteQuery<int, int[]>(
            client,
            QueryKey.Create("feed"),
            (page, _) => Task.FromResult(new[] { page * 10, page * 10 + 1 }),
            PageOptions);
        var changed = new List<string>();
        ((INotifyPropertyChanged)feed).PropertyChanged += (_, e) => { lock (changed) { changed.Add(e.PropertyName!); } };

        await TestUtil.WaitUntilAsync(() => feed.PageCount == 1);
        Assert.True(feed.IsSuccess);
        Assert.True(feed.HasNextPage);
        Assert.True(feed.FetchNextPageCommand.CanExecute(null));
        Assert.False(feed.FetchPreviousPageCommand.CanExecute(null));

        feed.FetchNextPageCommand.Execute(null);
        await TestUtil.WaitUntilAsync(() => feed.PageCount == 2);
        await feed.FetchNextPageAsync();

        Assert.Equal(3, feed.PageCount);
        Assert.False(feed.HasNextPage);
        Assert.False(feed.FetchNextPageCommand.CanExecute(null));
        Assert.Equal([0, 1, 10, 11, 20, 21], feed.Pages.SelectMany(p => p));
        lock (changed)
        {
            Assert.Contains(nameof(feed.Pages), changed);
            Assert.Contains(nameof(feed.HasNextPage), changed);
        }
    }

    [Fact]
    public async Task ObservableInfiniteQuery_stops_notifying_after_dispose()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var feed = new ObservableInfiniteQuery<int, int[]>(client, QueryKey.Create("feed-dispose"), (page, _) => Task.FromResult(new[] { page }), PageOptions);
        await TestUtil.WaitUntilAsync(() => feed.PageCount == 1);
        var notified = 0;
        feed.PropertyChanged += (_, _) => notified++;

        feed.Dispose();
        await client.InvalidateQueriesAsync(QueryKey.Create("feed-dispose"));
        await Task.Delay(50);

        Assert.Equal(0, notified);
        Assert.Equal(0, client.Cache.Describe(QueryKey.Create("feed-dispose"))!.ObserverCount);
    }
}
