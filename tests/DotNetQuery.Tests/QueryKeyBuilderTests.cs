using DotNetQuery;
using Xunit;

namespace DotNetQuery.Tests;

public class QueryKeyBuilderTests
{
    private sealed record User;

    [Fact]
    public void Append_derives_a_child_key_that_the_parent_is_a_prefix_of()
    {
        var users = QueryKey.Create("users");
        var user42 = users.Append(42);
        var user42Posts = user42.Append("posts");

        Assert.Equal(QueryKey.Create("users", 42), user42);
        Assert.Equal(QueryKey.Create("users", 42, "posts"), user42Posts);
        Assert.True(users.IsPrefixOf(user42Posts));
        Assert.True(user42.IsPrefixOf(user42Posts));
    }

    [Fact]
    public void Append_with_no_segments_returns_the_same_key()
    {
        var key = QueryKey.Create("users");
        Assert.Same(key, key.Append());
    }

    [Fact]
    public void Append_does_not_mutate_the_source_key()
    {
        var users = QueryKey.Create("users");
        _ = users.Append(1);
        Assert.Equal(QueryKey.Create("users"), users);
    }

    [Fact]
    public void Builder_constructs_a_key_from_chained_segments()
    {
        var key = QueryKey.Builder("users").Add(42).Add("posts").Build();
        Assert.Equal(QueryKey.Create("users", 42, "posts"), key);
    }

    [Fact]
    public void Builder_seed_and_AddRange_compose()
    {
        var key = QueryKey.Builder("a", "b").AddRange("c", "d").Build();
        Assert.Equal(QueryKey.Create("a", "b", "c", "d"), key);
        Assert.Equal(4, QueryKey.Builder("a", "b").AddRange("c", "d").Count);
    }

    [Fact]
    public void Generic_builder_prefixes_the_type_name()
    {
        var typed = QueryKey.Builder<User>("profile", 7).Build();
        Assert.Equal(QueryKey.Create<User>("profile", 7), typed);
        Assert.Equal("User", typed[0]);
    }

    [Fact]
    public void Build_throws_when_empty()
    {
        Assert.Throws<InvalidOperationException>(() => QueryKey.Builder().Build());
    }
}
