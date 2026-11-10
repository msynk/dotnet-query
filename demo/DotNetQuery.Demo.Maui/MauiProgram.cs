using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetQuery.Sample.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// DotNetQuery: the client, the sample data source, and the page (constructor-injected).
		builder.Services.AddDotNetQuery(options =>
		{
			options.DefaultStaleTime = TimeSpan.FromSeconds(5);
			options.DefaultRetryCount = 1;
		});
		builder.Services.AddSingleton<TodoService>();
		builder.Services.AddTransient<MainPage>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
