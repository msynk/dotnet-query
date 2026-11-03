using Bunit;
using DotNetQuery;
using DotNetQuery.DevTools.Blazor;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotNetQuery.Blazor.Tests;

public class DevToolsPanelTests : TestContext
{
    public DevToolsPanelTests()
    {
        Services.AddDotNetQuery(o => o.DefaultRetryCount = 0);
    }

    [Fact]
    public void Panel_renders_header_and_cached_queries()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        client.SetQueryData(QueryKey.Create("seed", 1), "value");

        var cut = RenderComponent<QueryDevToolsPanel>();

        Assert.Contains("DotNetQuery DevTools", cut.Markup);
        Assert.Contains("seed", cut.Markup);
        Assert.Contains("1 queries", cut.Markup);
    }

    [Fact]
    public void Panel_remove_button_evicts_the_query()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var key = QueryKey.Create("removable");
        client.SetQueryData(key, 1);

        var cut = RenderComponent<QueryDevToolsPanel>();
        Assert.Contains("removable", cut.Markup);

        // Click the Remove action button in the query's row.
        var removeButton = cut.FindAll("button").First(b => b.TextContent.Trim() == "Remove");
        removeButton.Click();

        cut.WaitForAssertion(() => Assert.Equal(0, client.Cache.Count));
    }

    [Fact]
    public void Selecting_a_query_shows_its_data_preview()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        client.SetQueryData(QueryKey.Create("profile"), new { Name = "Ada" });

        var cut = RenderComponent<QueryDevToolsPanel>();
        cut.FindAll("tbody tr").First(r => r.TextContent.Contains("profile")).Click();

        cut.WaitForAssertion(() => Assert.Contains("\"name\": \"Ada\"", cut.Find("pre.dnq-json").TextContent));
    }

    [Fact]
    public async Task Mutations_tab_lists_the_mutation_history()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var cut = RenderComponent<QueryDevToolsPanel>(ps => ps.Add(p => p.RefreshThrottle, TimeSpan.Zero));

        await client.MutateAsync<string, int>((s, _) => Task.FromResult(s.Length), "hello", new MutationOptions<string, int> { MutationKey = QueryKey.Create("greet") });
        cut.FindAll("button.dnq-tab").First(b => b.TextContent.StartsWith("Mutations")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("greet", cut.Markup);
            Assert.Contains("Success", cut.Markup);
        });
    }

    [Fact]
    public void Floating_panel_starts_collapsed_behind_a_toggle()
    {
        var cut = RenderComponent<QueryDevToolsPanel>(ps => ps.Add(p => p.Floating, true));
        Assert.Empty(cut.FindAll(".dnq-devtools"));

        cut.Find("button.dnq-fab").Click();

        Assert.NotEmpty(cut.FindAll(".dnq-devtools.dnq-floating"));
    }

    [Fact]
    public void Panel_refreshes_live_when_the_cache_changes()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var cut = RenderComponent<QueryDevToolsPanel>(ps => ps.Add(p => p.RefreshThrottle, TimeSpan.Zero));

        client.SetQueryData(QueryKey.Create("late-arrival"), 1);

        cut.WaitForAssertion(() => Assert.Contains("late-arrival", cut.Markup));
    }

    [Fact]
    public void Disabled_panel_renders_nothing()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        client.SetQueryData(QueryKey.Create("secret-key"), 1);

        var cut = RenderComponent<QueryDevToolsPanel>(ps => ps.Add(p => p.Enabled, false));

        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void ShowData_off_hides_cached_values()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        client.SetQueryData(QueryKey.Create("account"), "IBAN DE00 1234");

        var cut = RenderComponent<QueryDevToolsPanel>(ps => ps.Add(p => p.ShowData, false));
        cut.FindAll("tbody tr").First(r => r.TextContent.Contains("account")).Click();

        Assert.Contains("hidden", cut.Markup);
        Assert.DoesNotContain("IBAN", cut.Markup);
    }

    [Fact]
    public void Tree_view_and_status_filter_narrow_the_query_list()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        client.SetQueryData(QueryKey.Create("todos", 1), 1);
        client.SetQueryData(QueryKey.Create("users", 1), 1);

        var cut = RenderComponent<QueryDevToolsPanel>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Tree").Click();

        Assert.NotEmpty(cut.FindAll(".dnq-tree li"));
        Assert.Contains("\"todos\"", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "List").Click();
        cut.Find("select.dnq-filter").Change("Error");
        Assert.Empty(cut.FindAll(".dnq-table tbody tr"));
    }

    [Fact]
    public async Task Selecting_a_query_shows_its_timeline_and_reset_action()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        var cut = RenderComponent<QueryDevToolsPanel>(ps => ps.Add(p => p.RefreshThrottle, TimeSpan.Zero));
        await client.FetchAsync(QueryKey.Create("traced"), _ => Task.FromResult(1));
        cut.WaitForAssertion(() => Assert.Contains("traced", cut.Markup));

        cut.FindAll("tbody tr").First(r => r.TextContent.Contains("traced")).Click();

        cut.WaitForAssertion(() => Assert.Contains("completed", cut.Find(".dnq-timeline").TextContent));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Reset");
    }

    [Fact]
    public void Snapshots_tab_takes_and_restores_snapshots()
    {
        var client = Services.GetRequiredService<IQueryClient>();
        client.SetQueryData(QueryKey.Create("value"), 1);
        var cut = RenderComponent<QueryDevToolsPanel>();
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Snapshots", StringComparison.Ordinal)).Click();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Take snapshot").Click();
        client.SetQueryData(QueryKey.Create("value"), 2);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Restore").Click();

        Assert.Equal(1, client.GetQueryData<int>(QueryKey.Create("value")).Data);
    }
}
