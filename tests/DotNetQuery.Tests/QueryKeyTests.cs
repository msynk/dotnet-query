using DotNetQuery;
using Xunit;

namespace DotNetQuery.Tests;

public class QueryKeyTests
{
    [Fact]
    public void Equal_keys_are_equal_and_share_hash_code()
    {
        var a = QueryKey.Create("users", 42);
        var b = QueryKey.Create("users", 42);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Different_keys_are_not_equal()
    {
        Assert.NotEqual(QueryKey.Create("users", 1), QueryKey.Create("users", 2));
        Assert.NotEqual(QueryKey.Create("users"), QueryKey.Create("users", 1));
        Assert.True(QueryKey.Create("a") != QueryKey.Create("b"));
    }

    [Fact]
    public void Create_generic_prefixes_the_type_name()
    {
        var key = QueryKey.Create<User>("profile", 7);

        Assert.Equal(3, key.Count);
        Assert.Equal(nameof(User), key[0]);
        Assert.Equal("profile", key[1]);
        Assert.Equal(7, key[2]);
    }

    [Fact]
    public void Empty_key_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => QueryKey.Create());
    }

    [Fact]
    public void IsPrefixOf_matches_hierarchical_descendants()
    {
        var parent = QueryKey.Create("users");
        var child = QueryKey.Create("users", 42);

        Assert.True(parent.IsPrefixOf(child));
        Assert.True(parent.IsPrefixOf(parent));
        Assert.False(child.IsPrefixOf(parent));
        Assert.False(QueryKey.Create("posts").IsPrefixOf(child));
    }

    [Fact]
    public void Mutating_source_array_does_not_change_the_key()
    {
        var segments = new object?[] { "users", 1 };
        var key = QueryKey.Create(segments);
        segments[1] = 999;

        Assert.Equal(QueryKey.Create("users", 1), key);
    }

    [Fact]
    public void ToString_is_readable()
    {
        Assert.Equal("[\"users\", 42]", QueryKey.Create("users", 42).ToString());
    }

    private sealed record User(int Id);
}
