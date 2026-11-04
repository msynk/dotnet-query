using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bunit;
using DotNetQuery;
using DotNetQuery.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class WebLocksMutationQueueLockTests : TestContext
{
    private const string ModulePath = "./_content/DotNetQuery.Blazor/dotnetquery-locks.js";

    [Fact]
    public async Task Acquire_waits_for_the_browser_lock_and_release_frees_it()
    {
        var module = JSInterop.SetupModule(ModulePath);
        var grant = module.Setup<bool>("acquire", _ => true);
        module.SetupVoid("release", _ => true).SetVoidResult();
        var locks = new WebLocksMutationQueueLock(Services.GetRequiredService<IJSRuntime>(), "todos-queue");

        var acquiring = locks.AcquireAsync().AsTask();
        Assert.False(acquiring.IsCompleted);
        grant.SetResult(true);
        var lease = await acquiring;
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        var acquire = Assert.Single(module.Invocations, i => i.Identifier == "acquire");
        Assert.Equal("todos-queue", acquire.Arguments[0]);
        var release = Assert.Single(module.Invocations, i => i.Identifier == "release");
        Assert.Equal(acquire.Arguments[1], release.Arguments[0]);
    }

    [Fact]
    public async Task Cancelling_a_wait_cancels_the_browser_request()
    {
        var module = JSInterop.SetupModule(ModulePath);
        module.Setup<bool>("acquire", _ => true);
        module.SetupVoid("cancel", _ => true).SetVoidResult();
        var locks = new WebLocksMutationQueueLock(Services.GetRequiredService<IJSRuntime>());
        using var cts = new CancellationTokenSource();

        var acquiring = locks.AcquireAsync(cts.Token).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring);
        Assert.Contains(module.Invocations, i => i.Identifier == "cancel");
        Assert.Equal(WebLocksMutationQueueLock.DefaultName, module.Invocations.First(i => i.Identifier == "acquire").Arguments[0]);
    }

    [Fact]
    public async Task A_queue_with_the_lock_replays_only_while_holding_it()
    {
        var module = JSInterop.SetupModule(ModulePath);
        var grant = module.Setup<bool>("acquire", _ => true);
        module.SetupVoid("release", _ => true).SetVoidResult();
        var connectivity = new ConnectivityProvider();
        var sent = new List<string>();
        using var queue = new PersistentMutationQueue(new InMemoryQueryPersister(), connectivity,
                new PersistentMutationQueueOptions { ReplayLock = new WebLocksMutationQueueLock(Services.GetRequiredService<IJSRuntime>()) })
            .RegisterHandler("add", (string title, CancellationToken _) => { sent.Add(title); return Task.CompletedTask; }, StringInfo);

        var enqueue = queue.EnqueueAsync("add", "milk");
        await Task.Delay(50);
        Assert.Empty(sent);           // stored, waiting for the lock
        Assert.Equal(1, queue.PendingCount);

        grant.SetResult(true);
        await enqueue;

        Assert.Equal(["milk"], sent);
        Assert.Contains(module.Invocations, i => i.Identifier == "release");
    }

    private static readonly JsonTypeInfo<string> StringInfo = (JsonTypeInfo<string>)CreateOptions().GetTypeInfo(typeof(string));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
