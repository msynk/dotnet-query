using DotNetQuery;
using DotNetQuery.Mvvm;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Tests;

public class InitialAndPlaceholderDataTests
{
    private readonly FakeTimeProvider _time = new();
    private int _fetches;

    private QueryClient CreateClient() => new(TestUtil.NoRetry(o => o.DefaultStaleTime = TimeSpan.FromMinutes(1)), _time);

    private QueryDefinition<string> Definition(string key = "item") =>
        new(QueryKey.Create(key), _ => Task.FromResult($"fetched {Interlocked.Increment(ref _fetches)}"));

    [Fact]
    public async Task Fresh_initial_data_is_served_without_a_request()
    {
        using var client = CreateClient();

        var result = await client.FetchAsync(Definition().WithInitialData("seed"));

        Assert.Equal("seed", result.Data);
        Assert.Equal(QueryStatus.Success, result.Status);
        Assert.Equal(_time.GetUtcNow(), result.DataUpdatedAt);
        Assert.Equal(0, _fetches);
    }

    [Fact]
    public async Task Stale_initial_data_is_shown_and_then_refetched()
    {
        using var client = CreateClient();
        var definition = Definition().WithInitialData("seed", _time.GetUtcNow() - TimeSpan.FromMinutes(5));
        using var observer = client.Watch(definition);
        Assert.Equal("seed", observer.CurrentState.Data); // visible before any fetch

        var result = await client.FetchAsync(definition);

        Assert.Equal("fetched 1", result.Data);
        Assert.Equal(1, _fetches);
    }

    [Fact]
    public async Task Initial_data_never_overwrites_existing_data_or_errors()
    {
        using var client = CreateClient();
        await client.FetchAsync(Definition("has-data"));
        await client.FetchAsync<string>(QueryKey.Create("failed"), _ => throw new InvalidOperationException("down"));

        Assert.False(client.SeedInitialData(Definition("has-data").WithInitialData("seed")));
        Assert.False(client.SeedInitialData(Definition("failed").WithInitialData("seed")));

        Assert.Equal("fetched 1", client.GetQueryData<string>(QueryKey.Create("has-data")).Data);
        Assert.True(client.GetQueryData<string>(QueryKey.Create("failed")).IsError);
    }

    [Fact]
    public async Task Initial_data_factory_runs_lazily_and_ensure_returns_it()
    {
        using var client = CreateClient();
        var produced = 0;
        var definition = Definition() with { InitialData = () => { produced++; return "lazy"; } };
        Assert.Equal(0, produced);

        var data = await client.EnsureQueryDataAsync(definition);

        Assert.Equal("lazy", data);
        Assert.Equal(1, produced);
        Assert.Equal(0, _fetches);
    }

    [Fact]
    public async Task Reset_restores_the_initial_data()
    {
        using var client = CreateClient();
        var definition = Definition().WithInitialData("seed", _time.GetUtcNow() - TimeSpan.FromMinutes(5));
        await client.FetchAsync(definition);
        Assert.Equal("fetched 1", client.GetQueryData(definition).Data);

        await client.ResetQueriesAsync(QueryFilter.ForKey(definition.Key));

        var state = client.GetQueryData(definition);
        Assert.Equal("seed", state.Data);
        Assert.Equal(QueryStatus.Success, state.Status);
    }

    [Fact]
    public async Task Placeholder_is_shown_while_pending_but_never_cached()
    {
        using var client = CreateClient();
        var release = TestUtil.NewSignal<string>();
        var definition = new QueryDefinition<string>(QueryKey.Create("slow"), _ => release.Task).WithPlaceholderData("placeholder");
        using var observer = client.Watch(definition);

        Assert.True(observer.CurrentResult.IsPlaceholderData);
        Assert.Equal("placeholder", observer.CurrentResult.Data);
        Assert.Equal(QueryStatus.Success, observer.CurrentResult.Status); // idle, not fetching
        Assert.False(client.GetQueryData(definition).HasData);

        var fetch = client.FetchAsync(definition);
        Assert.Equal(QueryStatus.Refetching, observer.CurrentResult.Status);
        Assert.True(observer.CurrentResult.IsFetching);
        Assert.True(observer.CurrentResult.IsPlaceholderData);

        release.SetResult("real");
        await fetch;

        Assert.False(observer.CurrentResult.IsPlaceholderData);
        Assert.Equal("real", observer.CurrentResult.Data);
    }

    [Fact]
    public async Task Placeholder_is_not_shown_for_errors()
    {
        using var client = CreateClient();
        var definition = new QueryDefinition<string>(QueryKey.Create("broken"), _ => throw new InvalidOperationException("down"))
            .WithPlaceholderData("placeholder");
        using var observer = client.Watch(definition);

        await client.FetchAsync(definition);

        Assert.False(observer.CurrentResult.IsPlaceholderData);
        Assert.True(observer.CurrentResult.IsLoadingError);
    }

    [Fact]
    public void Throwing_placeholder_factory_means_no_placeholder()
    {
        using var client = CreateClient();
        var definition = Definition() with { PlaceholderData = () => throw new InvalidOperationException("bug") };
        using var observer = client.Watch(definition);

        Assert.False(observer.CurrentResult.IsPlaceholderData);
        Assert.True(observer.CurrentResult.IsPending);
    }

    [Fact]
    public void Placeholder_is_produced_once()
    {
        using var client = CreateClient();
        var produced = 0;
        var definition = Definition() with { PlaceholderData = () => { produced++; return "p"; } };
        using var observer = client.Watch(definition);

        _ = observer.CurrentResult;
        _ = observer.CurrentResult;

        Assert.Equal(1, produced);
    }

    [Fact]
    public async Task Selector_observers_select_initial_and_placeholder_data()
    {
        using var client = CreateClient();
        var release = TestUtil.NewSignal<string>();
        var placeholder = new QueryDefinition<string>(QueryKey.Create("p"), _ => release.Task).WithPlaceholderData("abc");
        var initial = Definition("i").WithInitialData("seeded");

        using var selectedPlaceholder = client.Watch(placeholder, (string s) => s.Length);
        using var selectedInitial = client.Watch(initial, (string s) => s.ToUpperInvariant());

        Assert.Equal(3, selectedPlaceholder.CurrentResult.Data);
        Assert.True(selectedPlaceholder.CurrentResult.IsPlaceholderData);
        Assert.Equal("SEEDED", selectedInitial.CurrentState.Data);
        release.SetResult("x");
        await Task.Yield();
    }

    [Fact]
    public void QueryResult_WithPlaceholderData_only_applies_while_pending()
    {
        var key = QueryKey.Create("r");
        var pending = new QueryResult<string>(key, QueryState<string>.Idle).WithPlaceholderData("p");
        var loaded = new QueryResult<string>(key, new QueryState<string> { Status = QueryStatus.Success, Data = "d", DataUpdatedAt = DateTimeOffset.UnixEpoch })
            .WithPlaceholderData("p");

        Assert.True(pending.IsPlaceholderData);
        Assert.True(pending.IsSuccess);
        Assert.Equal(DateTimeOffset.MinValue, pending.DataUpdatedAt);
        Assert.False(loaded.IsPlaceholderData);
        Assert.Equal("d", loaded.Data);
    }

    [Fact]
    public async Task ObservableQuery_uses_initial_and_placeholder_data()
    {
        using var client = CreateClient();
        var release = TestUtil.NewSignal<string>();
        using var withPlaceholder = new ObservableQuery<string>(client, new QueryDefinition<string>(QueryKey.Create("mvvm-p"), _ => release.Task).WithPlaceholderData("p"));
        using var withInitial = new ObservableQuery<string>(client, Definition("mvvm-i").WithInitialData("seed"));

        Assert.True(withPlaceholder.IsPlaceholderData);
        Assert.Equal("p", withPlaceholder.Data);
        Assert.Equal("seed", withInitial.Data);
        Assert.False(withInitial.IsPlaceholderData);

        release.SetResult("real");
        await TestUtil.WaitUntilAsync(() => withPlaceholder.Data == "real");
        Assert.False(withPlaceholder.IsPlaceholderData);
        Assert.Equal(0, _fetches); // fresh initial data was not refetched
    }
}
