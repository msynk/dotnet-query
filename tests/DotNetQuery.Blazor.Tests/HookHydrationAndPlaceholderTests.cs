using Bunit;
using Bunit.TestDoubles;
using DotNetQuery;
using DotNetQuery.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class HookHydrationAndPlaceholderTests : TestContext
{
    private static int s_fetches;

    public HookHydrationAndPlaceholderTests()
    {
        Services.AddDotNetQueryBlazor(o =>
        {
            o.DefaultRetryCount = 0;
            o.DefaultStaleTime = TimeSpan.FromMinutes(5);
        });
    }

    private sealed class HookView : DotNetQueryComponentBase
    {
        [Parameter]
        public QueryDefinition<int> Definition { get; set; } = default!;

        private QueryObserver<int> _value = default!;

        protected override void OnParametersSet() => _value = UseQuery(Definition);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            var result = _value.CurrentResult;
            builder.AddContent(0, $"{result.Data}|{(result.IsPlaceholderData ? "placeholder" : "real")}");
        }
    }

    private sealed class NoPersistHookView : DotNetQueryComponentBase
    {
        [Parameter]
        public QueryDefinition<int> Definition { get; set; } = default!;

        private QueryObserver<int> _value = default!;

        protected override bool PersistQueryState => false;

        protected override void OnParametersSet() => _value = UseQuery(Definition);

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, _value.CurrentState.Data);
    }

    private sealed class OpenCountView : DotNetQueryComponentBase
    {
        public static int Renders;

        [Parameter]
        public QueryDefinition<List<(string Title, bool Done)>> Definition { get; set; } = default!;

        private QueryObserver<int> _open = default!;

        protected override void OnParametersSet() =>
            _open = UseQuery(Definition, todos => todos.Count(t => !t.Done), QueryStateProperties.Data);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            Interlocked.Increment(ref Renders);
            builder.AddContent(0, $"open:{_open.CurrentState.Data}");
        }
    }

    [Fact]
    public void UseQuery_with_a_selector_re_renders_only_when_the_selection_changes()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var key = QueryKey.Create("todos-selected");
        var definition = new QueryDefinition<List<(string Title, bool Done)>>(key, _ => Task.FromResult(new List<(string, bool)> { ("a", false), ("b", true) }));

        var cut = RenderComponent<OpenCountView>(ps => ps.Add(p => p.Definition, definition));
        cut.WaitForAssertion(() => Assert.Contains("open:1", cut.Markup));
        var renders = Volatile.Read(ref OpenCountView.Renders);

        // A change that does not affect the selection (one done item renamed) causes no re-render.
        client.SetQueryData(key, new List<(string, bool)> { ("a", false), ("b renamed", true) });
        Thread.Sleep(150); // give a (wrongly) scheduled re-render time to happen
        Assert.Equal(renders, Volatile.Read(ref OpenCountView.Renders));

        client.SetQueryData(key, new List<(string, bool)> { ("a", false), ("b", false) });
        cut.WaitForAssertion(() => Assert.Contains("open:2", cut.Markup));
    }

    private static QueryDefinition<int> Counted(string key, int value) =>
        new(QueryKey.Create(key), _ =>
        {
            Interlocked.Increment(ref s_fetches);
            return Task.FromResult(value);
        });

    [Fact]
    public void UseQuery_hydrates_from_prerendered_state_without_refetching()
    {
        var appState = this.AddFakePersistentComponentState();
        var definition = Counted("hook-hydrate", 1);
        appState.Persist(QueryStateHydration.StateKey(definition.Key), HydrationEnvelope.Create(99, DateTimeOffset.UtcNow));
        var before = Volatile.Read(ref s_fetches);

        var cut = RenderComponent<HookView>(ps => ps.Add(p => p.Definition, definition));

        cut.WaitForAssertion(() => Assert.Contains("99|real", cut.Markup));
        Assert.Equal(before, Volatile.Read(ref s_fetches));
    }

    [Fact]
    public void UseQuery_persists_fetched_data_for_the_interactive_render()
    {
        var appState = this.AddFakePersistentComponentState();
        var definition = Counted("hook-persist", 42);

        var cut = RenderComponent<HookView>(ps => ps.Add(p => p.Definition, definition));
        cut.WaitForAssertion(() => Assert.Contains("42|real", cut.Markup));

        appState.TriggerOnPersisting();

        Assert.True(appState.TryTake<string>(QueryStateHydration.StateKey(definition.Key), out var persisted));
        Assert.Equal(42, HydrationEnvelope.Data<int>(persisted!));
    }

    [Fact]
    public void Opting_out_of_persistence_skips_hydration_and_persisting()
    {
        var appState = this.AddFakePersistentComponentState();
        var definition = Counted("hook-no-persist", 7);
        appState.Persist(QueryStateHydration.StateKey(definition.Key), HydrationEnvelope.Create(99, DateTimeOffset.UtcNow));

        var cut = RenderComponent<NoPersistHookView>(ps => ps.Add(p => p.Definition, definition));
        cut.WaitForAssertion(() => Assert.Contains("7", cut.Markup));

        Assert.DoesNotContain("99", cut.Markup);
    }

    [Fact]
    public void UseQuery_shows_placeholder_then_real_data()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new QueryDefinition<int>(QueryKey.Create("hook-placeholder"), _ => release.Task).WithPlaceholderData(-1);

        var cut = RenderComponent<HookView>(ps => ps.Add(p => p.Definition, definition));
        Assert.Contains("-1|placeholder", cut.Markup);

        release.SetResult(5);
        cut.WaitForAssertion(() => Assert.Contains("5|real", cut.Markup));
    }

    [Fact]
    public void UseQuery_seeds_initial_data()
    {
        var definition = Counted("hook-initial", 3).WithInitialData(11);
        var before = Volatile.Read(ref s_fetches);

        var cut = RenderComponent<HookView>(ps => ps.Add(p => p.Definition, definition));

        Assert.Contains("11|real", cut.Markup);
        Assert.Equal(before, Volatile.Read(ref s_fetches)); // fresh for the 5-minute stale time
    }

    [Fact]
    public void Query_component_renders_placeholder_data_in_child_content()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new QueryDefinition<string>(QueryKey.Create("component-placeholder"), _ => release.Task).WithPlaceholderData("skeleton");

        var cut = RenderComponent<Query<string>>(ps => ps
            .Add(p => p.Definition, definition)
            .Add(p => p.Loading, "<p>loading</p>")
            .Add(p => p.ChildContent, r => $"<span>{r.Data}:{r.IsPlaceholderData}</span>"));

        Assert.Contains("skeleton:True", cut.Markup);
        Assert.DoesNotContain("loading", cut.Markup);

        release.SetResult("real");
        cut.WaitForAssertion(() => Assert.Contains("real:False", cut.Markup));
    }

    [Fact]
    public void Query_component_renders_the_empty_template_for_empty_results()
    {
        var empty = RenderComponent<Query<List<string>>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("empty-list"))
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult(new List<string>()))
            .Add(p => p.Empty, "<p>nothing here</p>")
            .Add(p => p.ChildContent, r => $"<span>{r.Data?.Count} items</span>"));
        var full = RenderComponent<Query<List<string>>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("full-list"))
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult(new List<string> { "a" }))
            .Add(p => p.Empty, "<p>nothing here</p>")
            .Add(p => p.ChildContent, r => $"<span>{r.Data?.Count} items</span>"));
        var custom = RenderComponent<Query<int>>(ps => ps
            .Add(p => p.Key, QueryKey.Create("zero"))
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult(0))
            .Add(p => p.IsEmpty, n => n == 0)
            .Add(p => p.Empty, "<p>no unread messages</p>")
            .Add(p => p.ChildContent, r => $"<span>{r.Data} unread</span>"));

        empty.WaitForAssertion(() => Assert.Contains("nothing here", empty.Markup));
        full.WaitForAssertion(() => Assert.Contains("1 items", full.Markup));
        custom.WaitForAssertion(() => Assert.Contains("no unread messages", custom.Markup));
    }

    [Fact]
    public void Query_component_seeds_initial_data()
    {
        var calls = 0;
        var definition = new QueryDefinition<string>(QueryKey.Create("component-initial"), _ => { calls++; return Task.FromResult("fetched"); })
            .WithInitialData("seed");

        var cut = RenderComponent<Query<string>>(ps => ps
            .Add(p => p.Definition, definition)
            .Add(p => p.ChildContent, r => $"<span>{r.Data}</span>"));

        Assert.Contains("seed", cut.Markup);
        Assert.Equal(0, calls);
    }
}
