using DotNetQuery;
using Xunit;

namespace DotNetQuery.Tests;

public class MutationExtrasTests
{
    [Fact]
    public async Task Mutation_defaults_apply_by_key_prefix_and_the_mutations_own_options_win()
    {
        var options = TestUtil.NoRetry()
            .SetMutationDefaults(new MutationDefaults { RetryCount = 1, ShouldRetry = (_, e) => e is TimeoutException })
            .SetMutationDefaults(QueryKey.Create("todos"), new MutationDefaults { RetryCount = 3, Invalidates = [QueryKey.Create("todos")] });
        using var client = new QueryClient(options);
        var todosCalls = 0;
        var otherCalls = 0;
        var ownCalls = 0;

        await client.CreateMutation<int, int>((_, _) => { todosCalls++; throw new TimeoutException(); }, new() { MutationKey = QueryKey.Create("todos", "add"), ThrowOnError = false }).MutateAsync(1);
        await client.CreateMutation<int, int>((_, _) => { otherCalls++; throw new TimeoutException(); }, new() { ThrowOnError = false }).MutateAsync(1);
        await client.CreateMutation<int, int>((_, _) => { ownCalls++; throw new TimeoutException(); }, new() { MutationKey = QueryKey.Create("todos"), RetryCount = 1, ThrowOnError = false }).MutateAsync(1);

        Assert.Equal(4, todosCalls);   // prefix default: 3 retries
        Assert.Equal(2, otherCalls);   // global default: 1 retry
        Assert.Equal(2, ownCalls);     // own RetryCount wins
        Assert.Equal([QueryKey.Create("todos")], client.CreateMutation<int, int>((v, _) => Task.FromResult(v), new() { MutationKey = QueryKey.Create("todos", "x") }).Options.Invalidates);
    }

    [Fact]
    public async Task Deduplicated_mutations_join_a_pending_run_with_equal_variables()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var release = TestUtil.NewSignal<int>();
        var calls = 0;
        var markRead = client.CreateMutation<int, int>((id, _) => { Interlocked.Increment(ref calls); return release.Task; }, new() { DeduplicatePending = true });

        var first = markRead.MutateAsync(7);
        var duplicate = markRead.MutateAsync(7);
        var other = markRead.MutateAsync(8);
        release.SetResult(1);

        Assert.Equal((await first).Data, (await duplicate).Data);
        await other;
        Assert.Equal(2, calls);

        await markRead.MutateAsync(7); // settled runs are not joined
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Progress_reports_reach_state_observers_and_the_mutation_cache()
    {
        using var client = new QueryClient(TestUtil.NoRetry());
        var halfway = TestUtil.NewSignal();
        var finish = TestUtil.NewSignal();
        var upload = client.CreateMutation<string, string>(async (file, progress, _) =>
        {
            progress.Report(0.5);
            halfway.SetResult();
            await finish.Task;
            progress.Report(1);
            return file;
        });
        var reported = new List<double?>();
        upload.StateChanged += s => { lock (reported) { reported.Add(s.Progress); } };

        var run = upload.MutateAsync("photo.jpg");
        await halfway.Task;
        Assert.Equal(0.5, upload.CurrentState.Progress);
        Assert.Equal(0.5, Assert.Single(client.Cache.Mutations.FindAll()).Progress);

        finish.SetResult();
        var result = await run;

        Assert.Equal("photo.jpg", result.Data);
        lock (reported)
        {
            Assert.Contains(0.5, reported);
            Assert.Contains(1.0, reported);
        }
    }
}
