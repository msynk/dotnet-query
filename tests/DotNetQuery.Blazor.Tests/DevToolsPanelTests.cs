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
}
