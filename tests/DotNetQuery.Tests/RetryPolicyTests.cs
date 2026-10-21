using DotNetQuery;
using Xunit;

namespace DotNetQuery.Tests;

public class RetryPolicyTests
{
    private static readonly Exception Err = new InvalidOperationException();

    [Fact]
    public void Constant_returns_the_same_delay_every_attempt()
    {
        var policy = RetryPolicy.Constant(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), policy(0, Err));
        Assert.Equal(TimeSpan.FromSeconds(2), policy(5, Err));
    }

    [Fact]
    public void Exponential_doubles_and_caps()
    {
        var policy = RetryPolicy.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(1), policy(0, Err));
        Assert.Equal(TimeSpan.FromSeconds(2), policy(1, Err));
        Assert.Equal(TimeSpan.FromSeconds(4), policy(2, Err));
        Assert.Equal(TimeSpan.FromSeconds(8), policy(3, Err));
        Assert.Equal(TimeSpan.FromSeconds(10), policy(4, Err)); // capped (would be 16s)
        Assert.Equal(TimeSpan.FromSeconds(10), policy(20, Err));
    }

    [Fact]
    public void ExponentialWithJitter_stays_within_bounds()
    {
        var policy = RetryPolicy.ExponentialWithJitter(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var delay = policy(attempt, Err);
            Assert.True(delay >= TimeSpan.Zero);
            Assert.True(delay <= TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void Default_is_usable()
    {
        Assert.NotNull(RetryPolicy.Default);
        var delay = RetryPolicy.Default(0, Err);
        Assert.True(delay >= TimeSpan.Zero);
    }
}
