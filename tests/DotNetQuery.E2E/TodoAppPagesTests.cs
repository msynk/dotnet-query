using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace DotNetQuery.E2E;

/// <summary>
/// End-to-end checks of the WebAssembly and Auto render modes and of the CRUD, large-list, grid and tenant pages.
/// </summary>
[Collection(TodoAppCollection.Name)]
public sealed class TodoAppPagesTests(TodoAppFixture app)
{
    [SkippableFact]
    public async Task WebAssembly_page_hydrates_prerendered_data_without_calling_the_api()
    {
        var page = await OpenAsync();
        var apiCalls = 0;
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/api/todos", StringComparison.Ordinal) && request.Method == "GET")
            {
                Interlocked.Increment(ref apiCalls);
            }
        };

        await page.GotoAsync($"{app.BaseUrl}/wasm");
        await Assertions.Expect(page.Locator(".render-mode")).ToHaveTextAsync("Rendering: WebAssembly", new() { Timeout = 30000 });

        await Assertions.Expect(page.Locator(".client-todos li").First).ToBeVisibleAsync();
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref apiCalls)); // the browser cache was hydrated from the prerender

        var title = $"Wasm {Guid.NewGuid():N}"[..14];
        await page.GetByPlaceholder("New client todo").FillAsync(title);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".client-todos li", new() { HasText = title })).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [SkippableFact]
    public async Task WebAssembly_page_shows_a_todo_another_user_added_through_signalr()
    {
        var page = await OpenAsync();
        await page.GotoAsync($"{app.BaseUrl}/wasm");
        await Assertions.Expect(page.Locator(".render-mode")).ToHaveTextAsync("Rendering: WebAssembly", new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".live-status")).ToHaveTextAsync("Live updates: Synchronized", new() { Timeout = 15000 });

        // Another user adds a todo through the API: the hub event invalidates the open page's list (its stale time is
        // 30 seconds, so only the push can make it refetch this soon).
        var title = $"Pushed {Guid.NewGuid():N}"[..15];
        await CreateTodoAsync(title);

        await Assertions.Expect(page.Locator(".client-todos li", new() { HasText = title })).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [SkippableFact]
    public async Task WebAssembly_page_keeps_working_offline_and_syncs_a_queued_todo_on_reconnect()
    {
        var page = await OpenAsync();
        await page.GotoAsync($"{app.BaseUrl}/wasm");
        await Assertions.Expect(page.Locator(".render-mode")).ToHaveTextAsync("Rendering: WebAssembly", new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".client-todos li").First).ToBeVisibleAsync();

        await page.Context.SetOfflineAsync(true);
        await Assertions.Expect(page.Locator(".connectivity")).ToHaveTextAsync("Offline");
        var title = $"Offline {Guid.NewGuid():N}"[..16];
        await page.GetByPlaceholder("New client todo").FillAsync(title);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();

        // Shown at once, waiting for the network; the server has not received it.
        await Assertions.Expect(page.Locator(".client-todos li", new() { HasText = $"{title} (saving…)" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".sync-status")).ToBeVisibleAsync();
        await Task.Delay(500);
        Assert.DoesNotContain(title, await ServerTodosAsync());

        await page.Context.SetOfflineAsync(false);

        await Assertions.Expect(page.Locator(".connectivity")).ToHaveTextAsync("Online");
        await Assertions.Expect(page.Locator(".sync-status")).ToHaveCountAsync(0, new() { Timeout = 10000 });
        await Assertions.Expect(page.Locator(".client-todos li", new() { HasText = $"{title} (saving…)" })).ToHaveCountAsync(0, new() { Timeout = 10000 });
        await Assertions.Expect(page.Locator(".client-todos li", new() { HasText = title })).ToHaveCountAsync(1);
        Assert.Contains(title, await ServerTodosAsync());
    }

    [SkippableFact]
    public async Task Auto_page_starts_interactive_on_the_server_with_data()
    {
        var page = await OpenAsync();

        await page.GotoAsync($"{app.BaseUrl}/auto");

        await Assertions.Expect(page.Locator(".render-mode")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("Rendering: (Server|WebAssembly)"), new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".client-todos li").First).ToBeVisibleAsync();
    }

    [SkippableFact]
    public async Task Editing_a_todo_saves_it_and_refreshes_the_list()
    {
        var id = await CreateTodoAsync($"Edit me {Guid.NewGuid():N}"[..16]);
        var page = await OpenAsync();
        await page.GotoAsync($"{app.BaseUrl}/todos/{id}");
        var input = page.Locator(".title-input");
        await Assertions.Expect(input).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^Edit me"), new() { Timeout = 15000 });
        var renamed = $"Renamed {Guid.NewGuid():N}"[..16];

        // Retry until the circuit is interactive (typing into a not-yet-interactive input is lost on hydration).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!page.Url.EndsWith('/') || page.Url.Contains("/todos/", StringComparison.Ordinal))
        {
            try
            {
                await input.FillAsync(renamed, new() { Timeout = 2000 });
                await page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync(new() { Timeout = 2000 });
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                // Navigated away mid-attempt (saved), or not interactive yet: check and retry.
            }

            await Task.Delay(500);
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Saving did not navigate back to the list.");
            }
        }

        await Assertions.Expect(page.Locator("li", new() { HasText = renamed })).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [SkippableFact]
    public async Task Large_list_virtualizes_and_loads_pages_while_scrolling()
    {
        var page = await OpenAsync();
        await page.GotoAsync($"{app.BaseUrl}/large");
        await Assertions.Expect(page.Locator(".large-list .row").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        Assert.True(await page.Locator(".large-list .row").CountAsync() < 100); // only visible rows are rendered

        for (var i = 0; i < 20 && !await page.Locator(".large-list .row", new() { HasText = "Row 1,0" }).First.IsVisibleAsync(); i++)
        {
            await page.Locator(".large-list").EvaluateAsync("el => el.scrollTop += 3000");
            await Task.Delay(300);
        }

        await Assertions.Expect(page.Locator(".large-list .row", new() { HasText = "Row 1,0" }).First).ToBeVisibleAsync(new() { Timeout = 15000 });
        var loaded = await page.Locator(".loaded-pages").InnerTextAsync();
        Assert.True(int.Parse(loaded.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture) >= 10, loaded);
    }

    [SkippableFact]
    public async Task Grid_pages_through_rows()
    {
        var page = await OpenAsync();
        await page.GotoAsync($"{app.BaseUrl}/grid");
        await Assertions.Expect(page.Locator(".rows-grid")).ToContainTextAsync("Row 0");

        var next = page.Locator("button.go-next");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await page.Locator(".rows-grid tbody tr").First.InnerTextAsync()).Contains("Row 10", StringComparison.Ordinal))
        {
            await next.ClickAsync();
            await Task.Delay(300);
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The grid did not page.");
            }
        }

        Assert.Contains("Row 10", await page.Locator(".rows-grid tbody tr").First.InnerTextAsync());
    }

    [SkippableFact]
    public async Task Switching_tenants_shows_the_other_tenants_data_and_drops_the_previous_one()
    {
        var page = await OpenAsync();
        await page.GotoAsync($"{app.BaseUrl}/tenants");
        await Assertions.Expect(page.Locator(".projects")).ToContainTextAsync("Contoso website");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await page.Locator(".projects").InnerTextAsync()).Contains("Fabrikam", StringComparison.Ordinal))
        {
            await page.Locator("button.tenant", new() { HasText = "fabrikam" }).ClickAsync();
            await Task.Delay(300);
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The tenant did not switch.");
            }
        }

        await Assertions.Expect(page.Locator(".projects")).Not.ToContainTextAsync("Contoso");
        await Assertions.Expect(page.Locator(".cached-tenants")).Not.ToContainTextAsync("contoso");
    }

    [SkippableFact]
    public async Task Static_ssr_page_streams_the_loading_template_and_then_the_data()
    {
        Skip.If(app.SkipReason is not null, app.SkipReason);
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };

        var html = await http.GetStringAsync("/ssr");

        // One streamed response: the Loading template first, then the rendered list in a <blazor-ssr> update.
        var loading = html.IndexOf("ssr-loading", StringComparison.Ordinal);
        var data = html.IndexOf("Learn DotNetQuery", StringComparison.Ordinal);
        Assert.True(loading >= 0, "The Loading template was not streamed first.");
        Assert.True(data > loading, "The data was not streamed after the Loading template.");
        Assert.Contains("<blazor-ssr>", html);

        var page = await OpenAsync();
        await page.GotoAsync($"{app.BaseUrl}/ssr");
        await Assertions.Expect(page.Locator(".ssr-todos li", new() { HasText = "Learn DotNetQuery" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".ssr-loading")).ToHaveCountAsync(0);
    }

    private async Task<IPage> OpenAsync()
    {
        Skip.If(app.SkipReason is not null, app.SkipReason);
        var context = await app.Browser!.NewContextAsync();
        return await context.NewPageAsync();
    }

    private async Task<string> ServerTodosAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        return await http.GetStringAsync("/api/todos");
    }

    private async Task<int> CreateTodoAsync(string title)
    {
        Skip.If(app.SkipReason is not null, app.SkipReason);
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        using var response = await http.PostAsJsonAsync("/api/todos", title);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetInt32();
    }
}
