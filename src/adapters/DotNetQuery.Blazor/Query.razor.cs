using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetQuery.Blazor;

/// <summary>
/// Declaratively fetches, caches and renders a query. Re-renders whenever the query's state changes,
/// refetches when its key or <see cref="Inputs"/> change, and (during prerendering) hands the data it
/// fetched to the interactive render so the page neither refetches nor flashes a loading state.
/// </summary>
/// <example>
/// <code>
/// &lt;Query Definition="TodoQueries.List(Api)" KeepPreviousData&gt;
///     &lt;Loading&gt;&lt;p&gt;Loading…&lt;/p&gt;&lt;/Loading&gt;
///     &lt;Error Context="error"&gt;&lt;p&gt;@error.Message&lt;/p&gt;&lt;/Error&gt;
///     &lt;ChildContent Context="todos"&gt;
///         @foreach (var todo in todos.Data!) { &lt;TodoRow Todo="todo" /&gt; }
///     &lt;/ChildContent&gt;
/// &lt;/Query&gt;
/// </code>
/// </example>
/// <typeparam name="TData">The type of data the query produces.</typeparam>
public partial class Query<TData> : ComponentBase, IDisposable
{
    private readonly CancellationTokenSource _disposed = new();
    private QueryObserver<TData>? _observer;
    private QueryResult<TData>? _previousResult;
    private object? _lastInputs;
    private bool _lastEnabled;
    private bool _needsFetch;
    private bool _needsRefetch;
    private QueryKey? _key;
    private Func<CancellationToken, Task<TData>>? _fetcher;
    private QueryOptions? _options;
    private PersistentComponentState? _applicationState;
    private PersistingComponentStateSubscription? _persisting;

    [Inject]
    private IQueryClient QueryClient { get; set; } = default!;

    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>
    /// The query to run: key, fetcher and options in one reusable value. Use this or
    /// <see cref="Key"/> + <see cref="Fetcher"/>.
    /// </summary>
    [Parameter]
    public QueryDefinition<TData>? Definition { get; set; }

    /// <summary>The key identifying the query (when not using <see cref="Definition"/>).</summary>
    [Parameter]
    public QueryKey? Key { get; set; }

    /// <summary>Delegate that performs the fetch (when not using <see cref="Definition"/>).</summary>
    [Parameter]
    public Func<CancellationToken, Task<TData>>? Fetcher { get; set; }

    /// <summary>Optional per-query options. When set, they take precedence over <see cref="QueryDefinition{T}.Options"/>.</summary>
    [Parameter]
    public QueryOptions? Options { get; set; }

    /// <summary>
    /// Optional dependency value describing the fetcher's inputs. When it changes between renders
    /// (by structural equality) while the key stays the same, the query refetches using the current
    /// fetcher. Combine several inputs with a tuple, e.g. <c>Inputs="@((page, filter))"</c>. Prefer putting
    /// inputs in the key instead, so each combination is cached separately.
    /// </summary>
    [Parameter]
    public object? Inputs { get; set; }

    /// <summary>
    /// When the key changes, keep rendering the previous key's data (with
    /// <see cref="QueryResult{T}.IsPlaceholderData"/> set) until the new key's first result arrives -
    /// flicker-free pagination, sorting and filtering.
    /// </summary>
    [Parameter]
    public bool KeepPreviousData { get; set; }

    /// <summary>
    /// Whether data fetched while prerendering is persisted into the page and used to hydrate the
    /// interactive render (default <see langword="true"/>). Disable it for data that must not be embedded in
    /// the HTML or cannot be serialized to JSON.
    /// </summary>
    [Parameter]
    public bool PersistState { get; set; } = true;

    /// <summary>
    /// Renders the query result. Receives every state unless <see cref="Loading"/> or <see cref="Error"/> is
    /// provided, in which case it only receives the states those templates do not handle.
    /// </summary>
    [Parameter]
    public RenderFragment<QueryResult<TData>>? ChildContent { get; set; }

    /// <summary>Optional template rendered while the query has neither data nor an error (first load).</summary>
    [Parameter]
    public RenderFragment? Loading { get; set; }

    /// <summary>Optional template rendered when the query failed and has no data to show.</summary>
    [Parameter]
    public RenderFragment<Exception>? Error { get; set; }

    /// <summary>Gets the result currently being rendered (including placeholder data).</summary>
    protected QueryResult<TData> CurrentResult
    {
        get
        {
            var result = _observer?.CurrentResult ?? default;
            return KeepPreviousData && _previousResult is { } previous ? result.KeepPreviousData(previous) : result;
        }
    }

    /// <summary>Forces a refetch of the rendered query, cancelling any fetch in flight.</summary>
    public Task RefetchAsync() => _key is null ? Task.CompletedTask : QueryClient.RefetchQueryAsync(_key);

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _applicationState = Services.GetService<PersistentComponentState>();
        if (_applicationState is not null)
        {
            _persisting = _applicationState.RegisterOnPersisting(PersistQueryState);
        }
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _key = Definition?.Key ?? Key
            ?? throw new InvalidOperationException($"{nameof(Query<TData>)} requires either {nameof(Definition)} or {nameof(Key)} and {nameof(Fetcher)}.");
        _fetcher = Definition?.Fetcher ?? Fetcher
            ?? throw new InvalidOperationException($"{nameof(Query<TData>)} requires a {nameof(Fetcher)} when {nameof(Key)} is used.");
        _options = Options ?? Definition?.Options;
        var enabled = _options?.Enabled ?? true;

        // Re-subscribe (and schedule a fetch) only when the key changes, so re-renders that merely
        // recreate the fetcher lambda don't trigger redundant fetches.
        if (_observer is not null && _observer.Key != _key)
        {
            _previousResult = KeepPreviousData ? CurrentResult : null;
            Unsubscribe();
        }

        if (_observer is null)
        {
            HydrateFromPrerender(_key);
            _observer = QueryClient.Watch<TData>(_key);
            _observer.StateChanged += OnStateChanged;
            _needsFetch = true;
            _lastInputs = Inputs;
        }
        else if (!Equals(_lastInputs, Inputs))
        {
            // Same key, but the declared inputs changed: refetch with the current fetcher, bypassing
            // staleness (and superseding a fetch for the old inputs) so the new inputs take effect.
            _lastInputs = Inputs;
            _needsRefetch = true;
        }
        else if (enabled && !_lastEnabled)
        {
            _needsFetch = true; // a dependent query just became enabled
        }

        _lastEnabled = enabled;
    }

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (!_needsFetch && !_needsRefetch)
        {
            return;
        }

        var force = _needsRefetch && !_needsFetch;
        _needsFetch = _needsRefetch = false;
        try
        {
            await QueryClient.FetchAsync(_key!, _fetcher!, _options, force, _disposed.Token);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            // Disposed while waiting; the shared fetch continues for other subscribers.
        }
    }

    private void OnStateChanged(QueryState<TData> state)
    {
        if (state.HasData || state.IsError)
        {
            _previousResult = null; // the new key has its own result now
        }

        _ = RenderAsync();
    }

    private async Task RenderAsync()
    {
        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // The renderer is gone (circuit closed); nothing to update.
        }
    }

    private void HydrateFromPrerender(QueryKey key)
    {
        if (PersistState && _applicationState is not null)
        {
            QueryStateHydration.TryHydrate<TData>(_applicationState, QueryClient, key);
        }
    }

    private Task PersistQueryState()
    {
        if (PersistState && _applicationState is not null && _key is not null)
        {
            QueryStateHydration.Persist<TData>(_applicationState, QueryClient, _key);
        }

        return Task.CompletedTask;
    }

    private void Unsubscribe()
    {
        if (_observer is not null)
        {
            _observer.StateChanged -= OnStateChanged;
            _observer.Dispose();
            _observer = null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _persisting?.Dispose();
        _persisting = null;
        Unsubscribe();
        _disposed.Cancel();
        _disposed.Dispose();
        GC.SuppressFinalize(this);
    }
}
