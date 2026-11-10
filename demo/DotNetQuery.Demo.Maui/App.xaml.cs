using DotNetQuery.Maui;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetQuery.Demo.Maui;

public partial class App : Application
{
	private readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// Resolve the page from DI so its IQueryClient / TodoService / ConnectivityProvider inject.
		var window = new Window(new NavigationPage(_services.GetRequiredService<MainPage>()));

		// Returning to the app refetches stale data, resuming from the background does too, and the device's
		// connectivity pauses and resumes queries.
		var lifecycle = new MauiQueryLifecycle(window, _services.GetRequiredService<AppLifecycleSignals>());
		window.Destroying += (_, _) => lifecycle.Dispose();
		return window;
	}
}