using System.Windows;
using DotNetQuery;
using DotNetQuery.WinUI;
using DotNetQuery.Wpf;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Xunit;
using Application = System.Windows.Application;

namespace DotNetQuery.Desktop.Tests;

/// <summary>A WPF application whose activation events tests can raise.</summary>
internal sealed class TestApplication : Application
{
    public void RaiseActivated() => OnActivated(EventArgs.Empty);

    public void RaiseDeactivated() => OnDeactivated(EventArgs.Empty);
}

public class LifecycleTests
{
    // One System.Windows.Application per AppDomain, created and used on a single STA thread.
    private static readonly StaThread Ui = new();
    private static readonly Lazy<TestApplication> App = new(() => Ui.Run(() => new TestApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown }));

    private static (QueryClient Client, AppLifecycleSignals Signals, Func<int> Fetches) CreateClient()
    {
        var focus = new FocusManager();
        var resume = new ResumeManager();
        var connectivity = new ConnectivityProvider();
        var client = new QueryClient(new QueryClientOptions { DefaultRetryCount = 0 }, focusManager: focus, resumeManager: resume, connectivity: connectivity);
        var fetches = 0;
        var observer = client.Watch<int>(QueryKey.Create("on-screen"));
        client.FetchAsync(QueryKey.Create("on-screen"), _ => Task.FromResult(Interlocked.Increment(ref fetches))).GetAwaiter().GetResult();
        GC.KeepAlive(observer);
        return (client, new AppLifecycleSignals(focus, resume, connectivity), () => Volatile.Read(ref fetches));
    }

    [Fact]
    public async Task Wpf_application_activation_after_deactivation_refetches_stale_queries()
    {
        var app = App.Value;
        var (client, signals, fetches) = CreateClient();
        using var _ = client;
        using var lifecycle = Ui.Run(() => new WpfQueryLifecycle(app, signals, monitorNetwork: false));

        Ui.Run(app.RaiseActivated);                  // the first activation at startup does not count
        await Task.Delay(50);
        Assert.Equal(1, fetches());

        Ui.Run(app.RaiseDeactivated);
        Ui.Run(app.RaiseActivated);
        await WaitUntilAsync(() => fetches() == 2);

        lifecycle.Dispose();
        Ui.Run(app.RaiseDeactivated);
        Ui.Run(app.RaiseActivated);
        await Task.Delay(50);
        Assert.Equal(2, fetches());                  // disposed: no longer forwarded
    }

    [Fact]
    public async Task Wpf_system_resume_refetches_stale_queries()
    {
        var app = App.Value;
        var (client, signals, fetches) = CreateClient();
        using var _ = client;
        using var lifecycle = Ui.Run(() => new WpfQueryLifecycle(app, signals, monitorNetwork: false));

        lifecycle.OnPowerModeChanged(PowerModes.Suspend);
        await Task.Delay(50);
        Assert.Equal(1, fetches());

        lifecycle.OnPowerModeChanged(PowerModes.Resume);
        await WaitUntilAsync(() => fetches() == 2);
    }

    [Fact]
    public void Wpf_lifecycle_reports_network_availability_when_asked()
    {
        var app = App.Value;
        var connectivity = new ConnectivityProvider();
        connectivity.SetOnline(false);
        var signals = new AppLifecycleSignals(connectivity: connectivity);

        using (Ui.Run(() => new WpfQueryLifecycle(app, signals)))
        {
            // The monitor reports the machine's current availability on start (a test machine has a network).
            Assert.Equal(System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(), connectivity.IsOnline);
        }
    }

    [Fact]
    public async Task WinUI_window_activation_states_map_to_focus_signals()
    {
        var (client, signals, fetches) = CreateClient();
        using var _ = client;

        WinUIQueryLifecycle.Forward(signals, WindowActivationState.CodeActivated);   // startup
        WinUIQueryLifecycle.Forward(signals, WindowActivationState.Deactivated);
        Assert.False(signals.IsActive);
        WinUIQueryLifecycle.Forward(signals, WindowActivationState.PointerActivated);
        Assert.True(signals.IsActive);

        await WaitUntilAsync(() => fetches() == 2);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(5);
        }
    }

    /// <summary>A dedicated STA thread (WPF objects must be created and used on one).</summary>
    private sealed class StaThread
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = [];

        public StaThread()
        {
            var thread = new Thread(() =>
            {
                foreach (var action in _work.GetConsumingEnumerable())
                {
                    action();
                }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public T Run<T>(Func<T> func)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() =>
            {
                try
                {
                    result.SetResult(func());
                }
                catch (Exception ex)
                {
                    result.SetException(ex);
                }
            });
            return result.Task.GetAwaiter().GetResult();
        }

        public void Run(Action action) => Run(() =>
        {
            action();
            return true;
        });
    }
}
