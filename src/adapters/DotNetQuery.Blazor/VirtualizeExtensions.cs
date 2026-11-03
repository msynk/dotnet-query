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
    /// <remarks>
    /// With <see cref="InfiniteQueryOptions{TParam, TPage}.MaxPages"/> the loaded pages are a sliding window: the
    /// provider tracks which row the window starts at, and loads previous pages again when Virtualize scrolls back
    /// before it (which needs <see cref="InfiniteQueryOptions{TParam, TPage}.GetPreviousPageParam"/>; without it, rows
    /// before the window come back empty).
    /// </remarks>
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

        // The row each page seen so far starts at, by page parameter.
        var starts = new Dictionary<PageKey<TParam>, int>();

        return async request =>
        {
            var needed = request.StartIndex + request.Count;
            var state = query.CurrentState.PageCount == 0
                ? await query.FetchAsync(request.CancellationToken)
                : query.CurrentState;
            var start = Locate(state);

            // Scrolled back before the window (MaxPages dropped its first pages): load backward.
            while (start > request.StartIndex && state.HasPreviousPage && !state.IsError)
            {
                var before = start;
                state = await query.FetchPreviousPageAsync(request.CancellationToken);
                start = Locate(state, seedFirstEndingAt: before);
                if (start >= before)
                {
                    break;
                }
            }

            // Progress is measured by where the window ends: with MaxPages it slides instead of growing.
            while (start + Count(state) < needed && state.HasNextPage && !state.IsError)
            {
                var end = start + Count(state);
                state = await query.FetchNextPageAsync(request.CancellationToken);
                start = Locate(state, seedLastStartingAt: end);
                if (start + Count(state) <= end)
                {
                    break;
                }
            }

            var rows = state.Data.Pages.SelectMany(items).ToArray();
            var lastPage = state.Data.Pages.Count > 0 ? state.Data.Pages[^1] : default;
            var total = lastPage is not null && totalCount?.Invoke(lastPage) is { } reported
                ? reported
                : start + rows.Length + (state.HasNextPage && lastPage is not null ? Math.Max(1, items(lastPage).Count) : 0);
            var skip = request.StartIndex - start;
            return new ItemsProviderResult<TItem>(skip < 0 ? [] : rows.Skip(skip).Take(request.Count), total);
        };

        int Count(InfiniteQueryState<TParam, TPage> state) => state.Data.Pages.Sum(page => items(page).Count);

        // Returns the row the loaded pages start at and records every page's start. The window is placed by the first
        // page already seen; a page just appended (or prepended) that nothing else places is placed by where the
        // previous window ended (or started). A window nothing places starts at row 0.
        int Locate(InfiniteQueryState<TParam, TPage> state, int? seedLastStartingAt = null, int? seedFirstEndingAt = null)
        {
            var pages = state.Data.Pages;
            var pageParams = state.Data.PageParams;
            lock (starts)
            {
                int? start = null;
                var offset = 0;
                for (var i = 0; i < pages.Count; i++)
                {
                    if (starts.TryGetValue(new PageKey<TParam>(pageParams[i]), out var known))
                    {
                        start = known - offset;
                        break;
                    }

                    offset += items(pages[i]).Count;
                }

                if (start is null && pages.Count > 0)
                {
                    if (seedLastStartingAt is { } lastStart)
                    {
                        start = lastStart - (Count(state) - items(pages[^1]).Count);
                    }
                    else if (seedFirstEndingAt is { } firstEnd)
                    {
                        start = firstEnd - items(pages[0]).Count;
                    }
                }

                var row = Math.Max(0, start ?? 0);
                var first = row;
                for (var i = 0; i < pages.Count; i++)
                {
                    starts[new PageKey<TParam>(pageParams[i])] = row;
                    row += items(pages[i]).Count;
                }

                return first;
            }
        }
    }

    // A page parameter as a dictionary key (a cursor may be null).
    private readonly record struct PageKey<TParam>(TParam Param);
}
