using Microsoft.Extensions.DependencyInjection;

namespace DotNetQuery.Sample.Maui;

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
		return new Window(new NavigationPage(_services.GetRequiredService<MainPage>()));
	}
}