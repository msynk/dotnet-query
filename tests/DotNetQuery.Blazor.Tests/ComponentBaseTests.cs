using Bunit;
using DotNetQuery;
using DotNetQuery.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class ComponentBaseTests : TestContext
{
    public ComponentBaseTests()
    {
        Services.AddDotNetQueryBlazor(o =>
        {
            o.DefaultRetryCount = 0;
            o.DefaultStaleTime = TimeSpan.FromMinutes(5);
        });
    }

    /// <summary>A hook-style component: one query keyed by a parameter, one mutation.</summary>
    private sealed class ItemView : DotNetQueryComponentBase
    {
        public static int Fetches;

        [Parameter]
        public int Id { get; set; }

        public Mutation<string, string> Rename { get; private set; } = default!;

        private QueryObserver<string> _item = default!;

        protected override void OnParametersSet()
        {
            _item = UseQuery(new QueryDefinition<string>(QueryKey.Create("item", Id), _ =>
            {
                Interlocked.Increment(ref Fetches);
                return Task.FromResult($"item {Id}");
            }));
            Rename = UseMutation<string, string>((name, _) => Task.FromResult(name));
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            builder.AddContent(0, $"{_item.CurrentState.Data}|{Rename.CurrentState.Status}");
    }

    [Fact]
    public void UseQuery_is_idempotent_per_call_site_and_re_keys_on_parameter_change()
    {
        ItemView.Fetches = 0;
        var client = Services.GetRequiredService<IQueryClient>();

        var cut = RenderComponent<ItemView>(ps => ps.Add(p => p.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("item 1", cut.Markup));

        cut.Render(); // re-render with the same key: no new subscription, no refetch
        Assert.Equal(1, ItemView.Fetches);
        Assert.Equal(1, client.Cache.Describe(QueryKey.Create("item", 1))!.ObserverCount);

        cut.SetParametersAndRender(ps => ps.Add(p => p.Id, 2));
        cut.WaitForAssertion(() => Assert.Contains("item 2", cut.Markup));
        Assert.Equal(0, client.Cache.Describe(QueryKey.Create("item", 1))!.ObserverCount);
        Assert.Equal(2, ItemView.Fetches);

        DisposeComponents();
        Assert.Equal(0, client.Cache.Describe(QueryKey.Create("item", 2))!.ObserverCount);
    }

    [Fact]
    public async Task UseMutation_re_renders_on_state_changes()
    {
        var cut = RenderComponent<ItemView>(ps => ps.Add(p => p.Id, 3));
        cut.WaitForAssertion(() => Assert.Contains("item 3|Idle", cut.Markup));

        await cut.InvokeAsync(() => cut.Instance.Rename.MutateAsync("renamed"));

        cut.WaitForAssertion(() => Assert.Contains("|Success", cut.Markup));
    }
}
