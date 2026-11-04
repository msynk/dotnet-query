using System.Text.Json.Serialization;
using Bunit;
using Bunit.TestDoubles;
using DotNetQuery;
using DotNetQuery.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public sealed record Reading(string Sensor, double Value);

[JsonSerializable(typeof(Reading[]))]
internal sealed partial class HydrationTestJson : JsonSerializerContext;

public class HydrationJsonTests : TestContext
{
    [Fact]
    public void Registered_source_generated_metadata_round_trips_prerendered_data()
    {
        Services.AddDotNetQueryBlazor(o => o.DefaultRetryCount = 0).UseHydrationJson(HydrationTestJson.Default);
        var appState = this.AddFakePersistentComponentState();
        var key = QueryKey.Create("readings");
        var server = new[] { new Reading("a", 1.5) };

        var cut = RenderComponent<Query<Reading[]>>(ps => ps
            .Add(p => p.Key, key)
            .Add(p => p.Fetcher, (CancellationToken _) => Task.FromResult(server))
            .Add(p => p.ChildContent, r => $"<span>{r.Data?.Length}</span>"));
        cut.WaitForAssertion(() => Assert.Contains("<span>1</span>", cut.Markup));
        appState.TriggerOnPersisting();

        Assert.True(appState.TryTake<string>(QueryStateHydration.StateKey(key), out var envelope));
        Assert.Equal(server, HydrationEnvelope.Data<Reading[]>(envelope!));
    }

    [Fact]
    public void The_JsonTypeInfo_overloads_persist_and_hydrate_with_the_original_timestamp()
    {
        Services.AddDotNetQueryBlazor(o => o.DefaultRetryCount = 0);
        var appState = this.AddFakePersistentComponentState();
        var state = Services.GetRequiredService<PersistentComponentState>();
        var key = QueryKey.Create("readings");
        var fetchedAt = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        using var server = new QueryClient();
        server.HydrateQueryData(key, new[] { new Reading("b", 2) }, fetchedAt);

        var persistedOk = false;
        state.RegisterOnPersisting(() =>
        {
            persistedOk = QueryStateHydration.Persist(state, server, key, HydrationTestJson.Default.ReadingArray);
            return Task.CompletedTask;
        });
        appState.TriggerOnPersisting();
        Assert.True(persistedOk);
        var persisted = appState.TryTake<string>(QueryStateHydration.StateKey(key), out var envelope);
        Assert.True(persisted);
        appState.Persist(QueryStateHydration.StateKey(key), envelope!);   // hand it to the "interactive" side

        using var browser = new QueryClient();
        Assert.True(QueryStateHydration.TryHydrate(state, browser, key, HydrationTestJson.Default.ReadingArray));
        var hydrated = browser.GetQueryData<Reading[]>(key);
        Assert.Equal([new Reading("b", 2)], hydrated.Data!);
        Assert.Equal(fetchedAt, hydrated.DataUpdatedAt);
    }
}
