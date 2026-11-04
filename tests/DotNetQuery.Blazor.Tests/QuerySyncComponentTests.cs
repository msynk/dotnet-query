using Bunit;
using DotNetQuery;
using DotNetQuery.Blazor;
using DotNetQuery.Synchronization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class QuerySyncComponentTests : TestContext
{
    private static readonly QueryKey Rooms = QueryKey.Create("rooms");

    public QuerySyncComponentTests()
    {
        Services.AddDotNetQueryBlazor(o =>
        {
            o.DefaultRetryCount = 0;
            o.DefaultStaleTime = TimeSpan.FromMinutes(5);
        }).AddCrossTabEventRelay(new QueryEventRelayOptions { ChannelName = "test" });
        Services.AddScoped(sp => new QueryEventSynchronizer(sp.GetRequiredService<IQueryClient>(), Source));
    }

    private InMemoryQueryEventSource Source { get; } = new();

    public sealed record Message(int Room, string Text);

    /// <summary>A chat room: maps the room's messages for as long as it is shown and renders the sync status.</summary>
    private sealed class RoomView : DotNetQueryComponentBase
    {
        [Inject]
        public QueryEventSynchronizer Sync { get; set; } = default!;

        [Parameter]
        public int Room { get; set; }

        protected override void OnParametersSet()
        {
            var room = Room;
            UseQueryEvents<Message>(Sync, "MessageAdded", m => m
                .Where(msg => msg.Room == room)
                .Update<List<string>>(Rooms.Append(room), (list, msg) => [.. list, msg.Text]),
                dependsOn: room);
        }

        // Called while rendering, so every re-render it causes reads the current status.
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, $"status:{UseQuerySyncStatus(Sync)}");
    }

    [Fact]
    public async Task UseQueryEvents_maps_events_while_the_component_lives_and_follows_its_dependency()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var sync = Services.GetRequiredService<QueryEventSynchronizer>();
        client.SetQueryData(Rooms.Append(1), new List<string>());
        client.SetQueryData(Rooms.Append(2), new List<string>());

        var cut = RenderComponent<RoomView>(ps => ps.Add(p => p.Room, 1));
        cut.Render();
        Assert.Equal(1, sync.GetStatistics().Mappings["MessageAdded"]); // once per call site, not per render

        await sync.ProcessAsync("MessageAdded", new Message(1, "hi"));
        await sync.ProcessAsync("MessageAdded", new Message(2, "elsewhere"));
        Assert.Equal(["hi"], client.GetQueryData<List<string>>(Rooms.Append(1)).Data);

        cut.SetParametersAndRender(ps => ps.Add(p => p.Room, 2)); // the mapping follows the room
        await sync.ProcessAsync("MessageAdded", new Message(2, "there"));
        Assert.Equal(["there"], client.GetQueryData<List<string>>(Rooms.Append(2)).Data);
        Assert.Equal(1, sync.GetStatistics().Mappings["MessageAdded"]);

        DisposeComponents();
        Assert.Empty(sync.GetStatistics().Mappings);
        Assert.Equal(0, Source.SubscriberCount("MessageAdded"));
    }

    [Fact]
    public async Task UseQuerySyncStatus_re_renders_on_status_changes()
    {
        var cut = RenderComponent<RoomView>(ps => ps.Add(p => p.Room, 1));
        Assert.Contains("status:Disconnected", cut.Markup);

        await Source.StartAsync();
        cut.WaitForAssertion(() => Assert.Contains("status:Synchronized", cut.Markup));

        Source.SetState(QueryEventSourceState.Reconnecting);
        cut.WaitForAssertion(() => Assert.Contains("status:Reconnecting", cut.Markup));
    }

    [Fact]
    public async Task The_cross_tab_relay_posts_notices_without_payloads_and_applies_notices_from_other_tabs()
    {
        var module = JSInterop.SetupModule("./_content/DotNetQuery.Blazor/dotnetquery-sync.js");
        var channel = module.SetupModule("open", _ => true);
        channel.Mode = JSRuntimeMode.Loose;
        var client = Services.GetRequiredService<IQueryClient>();
        var sync = Services.GetRequiredService<QueryEventSynchronizer>();
        sync.Map<Message>("MessageAdded", m => m.Invalidate(Rooms));
        await client.FetchAsync(Rooms.Append(1), _ => Task.FromResult(new List<string>()));

        RenderComponent<DotNetQueryCrossTabEvents>();
        var host = Services.GetRequiredService<QueryEventRelayHost>();
        Assert.True(host.Channel.IsConnected);

        Assert.Equal(QueryEventOutcome.Applied, await sync.ProcessAsync("MessageAdded", new Message(1, "secret text")));
        Assert.Equal(1, host.Relay.MessagesSent);
        var posted = (string)channel.Invocations["post"].Single().Arguments[0]!;
        Assert.Contains("\"ev\":\"MessageAdded\"", posted);
        Assert.DoesNotContain("secret text", posted); // notices never carry payloads

        await client.FetchAsync(Rooms.Append(1), _ => Task.FromResult(new List<string>()), forceRefetch: true);
        Assert.False(client.Cache.Describe(Rooms.Append(1))!.IsStale);
        host.Channel.OnMessage("""{"v":1,"ch":"test","o":"other-tab","id":"m1","op":1,"ev":"MessageAdded"}""");
        await sync.ProcessAsync("Barrier", 0);

        Assert.True(client.Cache.Describe(Rooms.Append(1))!.IsStale);
        Assert.Equal(1, host.Relay.MessagesApplied);
    }
}
