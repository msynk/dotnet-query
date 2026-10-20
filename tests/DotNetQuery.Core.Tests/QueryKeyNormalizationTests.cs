using System.Globalization;
using DotNetQuery;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class QueryKeyNormalizationTests
{
    private sealed record Todo;

    private sealed record User;

    [Fact]
    public void Generic_type_keys_do_not_collide()
    {
        var todos = QueryKey.Create<List<Todo>>();
        var users = QueryKey.Create<List<User>>();

        Assert.NotEqual(todos, users);
        Assert.Equal("List<Todo>", todos[0]);
        Assert.Equal("Dictionary<String,List<Todo>>", QueryKey.Create<Dictionary<string, List<Todo>>>()[0]);
    }

    [Fact]
    public void List_segments_compare_structurally()
    {
        var a = QueryKey.Create("todos", new[] { 1, 2 });
        var b = QueryKey.Create("todos", new List<int> { 1, 2 });
        var c = QueryKey.Create("todos", new[] { 2, 1 });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.True(QueryKey.Create("todos").IsPrefixOf(b));
    }

    [Fact]
    public void List_segments_are_snapshotted()
    {
        var ids = new List<int> { 1 };
        var key = QueryKey.Create("todos", ids);
        ids.Add(2);

        Assert.Equal(QueryKey.Create("todos", new[] { 1 }), key);
        Assert.Equal("[\"todos\", [1]]", key.ToString());
    }

    [Fact]
    public void ToString_escapes_strings_so_distinct_keys_never_share_a_string_form()
    {
        var tricky = QueryKey.Create("a\", \"b");
        var pair = QueryKey.Create("a", "b");

        Assert.NotEqual(tricky.ToString(), pair.ToString());
        Assert.Equal("[\"a\\\", \\\"b\"]", tricky.ToString());
    }

    [Fact]
    public void ToString_is_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var key = QueryKey.Create("price", 1.5, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), true);

            Assert.Equal("[\"price\", 1.5, 2026-01-02T03:04:05.0000000+00:00, true]", key.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Append_normalizes_list_segments()
    {
        var parent = QueryKey.Create("todos");

        Assert.Equal(QueryKey.Create("todos", new[] { "open" }), parent.Append(new List<string> { "open" }));
    }
}
