using DotNetQuery.Maui;
using DotNetQuery.Mvvm;

namespace DotNetQuery.Sample.Maui;

public partial class MainPage : ContentPage
{
	private static readonly QueryKey TodosKey = QueryKey.Create("todos");

	private readonly ObservableQuery<List<string>> _todos;

	public MainPage(IQueryClient client, TodoService service)
	{
		InitializeComponent();

		// The MAUI adapter creates an INotifyPropertyChanged view over the query, kicks off the initial
		// fetch, and marshals state changes onto the UI thread - bind straight to it (the Refetch button binds
		// to its RefetchCommand). Connectivity and focus/resume refetches are wired once, for the window, by
		// MauiQueryLifecycle in App.CreateWindow.
		_todos = client.CreateObservableQuery(TodosKey, service.GetTodosAsync);
		BindingContext = _todos;
	}

	protected override void OnHandlerChanging(HandlerChangingEventArgs args)
	{
		base.OnHandlerChanging(args);
		if (args.NewHandler is null)
		{
			// Page torn down: release the observer.
			_todos.Dispose();
		}
	}
}
