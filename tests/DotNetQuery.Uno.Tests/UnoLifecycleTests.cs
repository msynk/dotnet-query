using DotNetQuery;
using DotNetQuery.Uno;
using Windows.UI.Core;
using Xunit;

namespace DotNetQuery.Uno.Tests;

// Uno's net10.0 assemblies are reference implementations (they throw outside an Uno app head), so only the platform-free
// parts of the adapter are exercised here; the factories share their code with the WinUI adapter.
public class UnoLifecycleTests
{
    [Fact]
    public async Task Window_activation_states_map_to_focus_signals()
    {
        var focus = new FocusManager();
        using var client = new QueryClient(new QueryClientOptions { DefaultRetryCount = 0 }, focusManager: focus);
        var fetches = 0;
        using var observer = client.Watch<int>(QueryKey.Create("on-screen"));
        await client.FetchAsync(QueryKey.Create("on-screen"), _ => Task.FromResult(Interlocked.Increment(ref fetches)));
        var signals = new AppLifecycleSignals(focus);

        UnoQueryLifecycle.Forward(signals, CoreWindowActivationState.CodeActivated);   // startup
        UnoQueryLifecycle.Forward(signals, CoreWindowActivationState.Deactivated);
        Assert.False(signals.IsActive);
        UnoQueryLifecycle.Forward(signals, CoreWindowActivationState.PointerActivated);
        Assert.True(signals.IsActive);

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (Volatile.Read(ref fetches) < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "The focus signal did not refetch.");
            await Task.Delay(5);
        }
    }
}
