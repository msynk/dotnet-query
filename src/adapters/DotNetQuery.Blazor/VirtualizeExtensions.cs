using Microsoft.AspNetCore.Components.Web.Virtualization;

namespace DotNetQuery.Blazor;

/// <summary>Connects infinite queries to Blazor's <c>&lt;Virtualize ItemsProvider&gt;</c>.</summary>
public static class VirtualizeExtensions
{
    /// <summary>
    /// Creates an <see cref="ItemsProviderDelegate{TItem}"/> over an infinite query: Virtualize asks for the rows it is
    /// about to show, and the provider loads the pages needed to cover them (sequentially, through the cache, so
    /// scrolling back is instant and the pages are shared, persisted and invalidated like any query).
    /// </summary>
    /// <param name="query">The infinite query.</param>
    /// <param name="items">Extracts a page's rows.</param>
    /// <param name="totalCount">
    /// Extracts the total row count from a page when the API reports it (enables a correctly sized scrollbar). When
    /// <see langword="null"/>, the provider reports the loaded rows plus one more page while more pages exist, so
    /// scrolling keeps loading.
    /// </param>
    /// <example>
    /// <code>
    /// &lt;Virtualize ItemsProvider="_rows" Context="row"&gt;&lt;RowView Row="row" /&gt;&lt;/Virtualize&gt;
    /// @code {
    ///     protected override void OnInitialized() =&gt;
    ///         _rows = UseInfiniteQuery(Keys.Rows, Api.GetPageAsync, Options).ToItemsProvider(page =&gt; page.Items, page =&gt; page.Total);
    /// }
    /// </code>
    /// </example>
    public static ItemsProviderDelegate<TItem> ToItemsProvider<TParam, TPage, TItem>(
        this InfiniteQuery<TParam, TPage> query,
        Func<TPage, IReadOnlyList<TItem>> items,
        Func<TPage, int?>? totalCount = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(items);
        return async request =>
        {
            var needed = request.StartIndex + request.Count;
            var state = query.CurrentState.PageCount == 0
                ? await query.FetchAsync(request.CancellationToken)
                : query.CurrentState;

            while (Count(state) < needed && state.HasNextPage && !state.IsError)
            {
                var before = Count(state);
                state = await query.FetchNextPageAsync(request.CancellationToken);
                if (Count(state) <= before)
                {
                    break;
                }
            }

            var rows = state.Data.Pages.SelectMany(items).ToArray();
            var lastPage = state.Data.Pages.Count > 0 ? state.Data.Pages[^1] : default;
            var total = lastPage is not null && totalCount?.Invoke(lastPage) is { } reported
                ? reported
                : rows.Length + (state.HasNextPage && lastPage is not null ? Math.Max(1, items(lastPage).Count) : 0);
            var start = Math.Min(request.StartIndex, rows.Length);
            return new ItemsProviderResult<TItem>(rows.Skip(start).Take(request.Count), total);
        };

        int Count(InfiniteQueryState<TParam, TPage> state) => state.Data.Pages.Sum(page => items(page).Count);
    }
}
