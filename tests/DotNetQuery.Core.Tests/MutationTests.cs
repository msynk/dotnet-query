using DotNetQuery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DotNetQuery.Core.Tests;

public class MutationTests
{
    private static QueryClient CreateClient(FakeTimeProvider time) =>
        new(new QueryClientOptions { DefaultRetryDelay = RetryPolicy.Constant(TimeSpan.Zero) }, time);

    [Fact]
    public async Task Mutation_runs_and_returns_data()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);

        var result = await client.MutateAsync<int, string>(
            (v, _) => Task.FromResult($"created-{v}"),
            42);

        Assert.True(result.IsSuccess);
        Assert.Equal("created-42", result.Data);
        Assert.Equal(MutationStatus.Success, result.Status);
    }

    [Fact]
    public async Task Lifecycle_callbacks_fire_in_order_on_success()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var log = new List<string>();

        var result = await client.MutateAsync<int, int>(
            (v, _) => Task.FromResult(v * 2),
            5,
            new MutationOptions<int, int>
            {
                OnMutate = (v, _) => { log.Add($"mutate:{v}"); return ValueTask.FromResult<object?>("ctx"); },
                OnSuccess = (d, v, ctx, _) => { log.Add($"success:{d}:{ctx}"); return ValueTask.CompletedTask; },
                OnError = (_, _, _, _) => { log.Add("error"); return ValueTask.CompletedTask; },
                OnSettled = (d, e, _, _, _) => { log.Add($"settled:{d}:{e is null}"); return ValueTask.CompletedTask; },
            });

        Assert.Equal(10, result.Data);
        Assert.Equal(new[] { "mutate:5", "success:10:ctx", "settled:10:True" }, log);
    }

    [Fact]
    public async Task Optimistic_update_is_rolled_back_on_error()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var key = QueryKey.Create("counter");
        client.SetQueryData(key, 100);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.MutateAsync<int, int>(
                (_, _) => throw new InvalidOperationException("server rejected"),
                1,
                new MutationOptions<int, int>
                {
                    OnMutate = (_, _) =>
                    {
                        var previous = client.GetQueryData<int>(key).Data;
                        client.SetQueryData(key, 999); // optimistic
                        return ValueTask.FromResult<object?>(previous);
                    },
                    OnError = (_, _, ctx, _) =>
                    {
                        client.SetQueryData(key, (int)ctx!); // rollback
                        return ValueTask.CompletedTask;
                    },
                }));

        Assert.Equal(100, client.GetQueryData<int>(key).Data);
    }

    [Fact]
    public async Task Mutation_retries_until_success()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var attempts = 0;

        var result = await client.MutateAsync<int, string>(
            (_, _) =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new TimeoutException();
                }

                return Task.FromResult("ok");
            },
            0,
            new MutationOptions<int, string> { RetryCount = 5 });

        Assert.Equal(3, attempts);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Error_is_captured_when_ThrowOnError_is_false()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);

        var result = await client.MutateAsync<int, int>(
            (_, _) => throw new InvalidOperationException("boom"),
            0,
            new MutationOptions<int, int> { ThrowOnError = false });

        Assert.True(result.IsError);
        Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Fact]
    public async Task Reusable_mutation_notifies_observers()
    {
        var time = new FakeTimeProvider();
        using var client = CreateClient(time);
        var statuses = new List<MutationStatus>();

        var mutation = client.CreateMutation<int, int>((v, _) => Task.FromResult(v));
        mutation.StateChanged += s => statuses.Add(s.Status);

        await mutation.MutateAsync(1);

        Assert.Contains(MutationStatus.Pending, statuses);
        Assert.Contains(MutationStatus.Success, statuses);
    }
}
