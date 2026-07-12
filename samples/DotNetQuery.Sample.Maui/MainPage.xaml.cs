using DotNetQuery.Maui;
using DotNetQuery.Mvvm;

namespace DotNetQuery.Sample.Maui;

public partial class MainPage : ContentPage
{
	private static readonly QueryKey TodosKey = QueryKey.Create("todos");

	private readonly IQueryClient _client;
	private readonly ObservableQuery<List<string>> _todos;
	private readonly MauiConnectivityBridge _connectivity;

	public MainPage(IQueryClient client, TodoService service, ConnectivityProvider connectivity)
	{
		InitializeComponent();
		_client = client;

		// Drive DotNetQuery's offline pause/resume from device connectivity.
		_connectivity = new MauiConnectivityBridge(connectivity);

		// The MAUI adapter creates an INotifyPropertyChanged view over the query, kicks off the initial
		// fetch, and marshals state changes onto the UI thread — bind straight to it.
		_todos = client.CreateObservableQuery(TodosKey, service.GetTodosAsync);
		BindingContext = _todos;
	}

	private async void OnRefetchClicked(object? sender, EventArgs e)
	{
		// Invalidate → the active observer refetches; the fetch counter in each row bumps.
		await _client.InvalidateQueriesAsync(TodosKey);
	}

	protected override void OnHandlerChanging(HandlerChangingEventArgs args)
	{
		base.OnHandlerChanging(args);
		if (args.NewHandler is null)
		{
			// Page torn down: release the observer and connectivity subscription.
			_todos.Dispose();
			_connectivity.Dispose();
		}
	}
}
