using Microsoft.Playwright;
using Xunit;

namespace DotNetQuery.E2E;

/// <summary>
/// End-to-end checks of the Blazor integration in a real browser against the TodoApp demo (interactive server
/// rendering with prerendering).
/// </summary>
[Collection(TodoAppCollection.Name)]
public sealed class TodoAppTests(TodoAppFixture app)
{
    // Records whether any text ever appeared in the page, from before the first byte is parsed.
    private const string SeenTextScript = """
        window.__dnqSeen = new Set();
        const watch = ['Loading…'];
        new MutationObserver(() => {
            const text = document.body ? document.body.innerText : '';
            for (const w of watch) { if (text.includes(w)) window.__dnqSeen.add(w); }
        }).observe(document, { childList: true, subtree: true, characterData: true });
        """;

    [SkippableFact]
    public async Task Prerendered_data_is_hydrated_without_a_second_fetch_or_loading_flash()
    {
        var page = await OpenAsync();

        // The prerendered HTML already contains the data.
        Assert.Contains("Learn DotNetQuery", await page.ContentAsync());

        await WaitForInteractivityAsync(page);

        // The interactive circuit's cache was hydrated: the list query exists with zero fetches.
        var listRow = page.Locator(".dnq-table tbody tr", new() { HasText = "Success" }).First;
        await Assertions.Expect(listRow).ToBeVisibleAsync();
        Assert.Equal("0", (await listRow.Locator("td").Nth(4).InnerTextAsync()).Trim());
        Assert.False(await page.EvaluateAsync<bool>("() => window.__dnqSeen.has('Loading…')"));
    }

    [SkippableFact]
    public async Task Adding_a_todo_shows_it_optimistically_and_then_confirms_it()
    {
        var page = await OpenAsync();
        await WaitForInteractivityAsync(page);
        var title = $"Buy milk {Guid.NewGuid():N}"[..20];

        await page.GetByPlaceholder("New todo").FillAsync(title);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();

        // Visible at once (optimistic, "saving…"), then confirmed by the server.
        await Assertions.Expect(page.GetByText($"{title} (saving…)")).ToBeVisibleAsync(new() { Timeout = 2000 });
        await Assertions.Expect(page.GetByText($"{title} (saving…)")).ToBeHiddenAsync(new() { Timeout = 10000 });
        await Assertions.Expect(page.Locator("li", new() { HasText = title })).ToHaveCountAsync(1);
    }

    [SkippableFact]
    public async Task A_rejected_todo_is_rolled_back()
    {
        var page = await OpenAsync();
        await WaitForInteractivityAsync(page);
        var title = $"fail {Guid.NewGuid():N}"[..14];

        await page.GetByPlaceholder("New todo").FillAsync(title);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();

        await Assertions.Expect(page.GetByText($"{title} (saving…)")).ToBeVisibleAsync(new() { Timeout = 2000 });
        await Assertions.Expect(page.GetByText("(rolled back)")).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Assertions.Expect(page.Locator("li", new() { HasText = title })).ToHaveCountAsync(0);
    }

    [SkippableFact]
    public async Task Switching_filters_keeps_the_previous_list_visible_instead_of_a_spinner()
    {
        var page = await OpenAsync();
        await WaitForInteractivityAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();

        await Assertions.Expect(page.GetByText("Wire up the cache")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("li", new() { HasText = "Ship it" })).ToHaveCountAsync(0, new() { Timeout = 10000 });
        Assert.False(await page.EvaluateAsync<bool>("() => window.__dnqSeen.has('Loading…')"));
    }

    [SkippableFact]
    public async Task A_change_made_in_one_tab_appears_in_another_tab()
    {
        var first = await OpenAsync();
        var second = await first.Context.NewPageAsync();
        await second.GotoAsync(app.BaseUrl);
        await WaitForInteractivityAsync(first);
        await WaitForInteractivityAsync(second);
        var title = $"Synced {Guid.NewGuid():N}"[..16];

        await first.GetByPlaceholder("New todo").FillAsync(title);
        await first.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();

        // The second tab's data is fresh for 30 s and nothing else triggers a refetch there: only the
        // BroadcastChannel synchronization can bring the new todo in.
        await Assertions.Expect(second.Locator("li", new() { HasText = title }).First).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Assertions.Expect(second.GetByText($"{title} (saving…)")).ToBeHiddenAsync(new() { Timeout = 10000 });
    }

    private async Task<IPage> OpenAsync()
    {
        Skip.If(app.SkipReason is not null, app.SkipReason);
        var context = await app.Browser!.NewContextAsync();
        await context.AddInitScriptAsync(SeenTextScript);
        var page = await context.NewPageAsync();
        await page.GotoAsync(app.BaseUrl);
        return page;
    }

    // Events dispatched before the circuit is connected are lost, so poll: open the floating DevTools panel until
    // it actually opens (only an interactive component can react to the click).
    private static async Task WaitForInteractivityAsync(IPage page)
    {
        var header = page.GetByText("DotNetQuery DevTools");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await header.IsVisibleAsync())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The page did not become interactive.");
            }

            var fab = page.Locator(".dnq-fab");
            if (await fab.IsVisibleAsync())
            {
                await fab.ClickAsync();
            }

            await Task.Delay(250);
        }
    }
}
