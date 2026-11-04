using DotNetQuery;
using DotNetQuery.Tests.GeneratedExtras;
using Xunit;

namespace DotNetQuery.Tests.GeneratedExtras
{
    public sealed record FeedPage(int[] Items, int? Next);

    public sealed record Widget(int Id);

    public sealed record Gadget(int Id);

    [GenerateQueries]
    public interface IFeedApi
    {
        [InfiniteQueryMethod("offset")]
        Task<FeedPage> GetFeedAsync(string topic, int offset, CancellationToken cancellationToken);

        Task<T> GetItemAsync<T>(int id, CancellationToken cancellationToken)
            where T : class;

        [MutationMethod(Invalidates = [nameof(GetFeedAsync)])]
        Task<int> PostAsync(string topic, string text);
    }

    // A thin wrapper class, the shape used over a Kiota client (which generates request builders, not an interface).
    [GenerateQueries(KeyPrefix = "kiota")]
    public class KiotaUsersWrapper
    {
        public int Calls { get; private set; }

        public virtual Task<string> GetUserAsync(int id, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult($"user {id}");
        }
    }
}

namespace DotNetQuery.Tests
{
    public class GeneratedQueriesExtrasTests
    {
        private sealed class FakeFeedApi : IFeedApi
        {
            public List<string> Calls { get; } = [];

            public Task<FeedPage> GetFeedAsync(string topic, int offset, CancellationToken cancellationToken)
            {
                Calls.Add($"{topic}@{offset}");
                return Task.FromResult(new FeedPage([offset, offset + 1], offset < 4 ? offset + 2 : null));
            }

            public Task<T> GetItemAsync<T>(int id, CancellationToken cancellationToken)
                where T : class
            {
                Calls.Add($"{typeof(T).Name}:{id}");
                return Task.FromResult((T)(object)(typeof(T) == typeof(Widget) ? new Widget(id) : new Gadget(id)));
            }

            public Task<int> PostAsync(string topic, string text) => Task.FromResult(1);
        }

        [Fact]
        public async Task Generated_infinite_queries_page_through_the_api_and_are_invalidated_by_mutations()
        {
            using var client = new QueryClient(TestUtil.NoRetry());
            var api = new FakeFeedApi();
            var options = new InfiniteQueryOptions<int, FeedPage>
            {
                InitialPageParam = 0,
                GetNextPageParam = (last, _, _) => last.Next is { } next ? NextPageParam.Of(next) : NextPageParam.None<int>(),
            };

            using var feed = FeedApiQueries.GetFeed(client, api, options, "news");
            await feed.FetchAsync();
            while (feed.HasNextPage)
            {
                await feed.FetchNextPageAsync();
            }

            Assert.Equal(["news@0", "news@2", "news@4"], api.Calls);
            Assert.Equal(QueryKey.Create("FeedApi", "GetFeed", "news"), FeedApiQueries.Keys.GetFeed("news"));
            Assert.Equal([0, 1, 2, 3, 4, 5], feed.CurrentState.Data!.Items(p => p.Items));

            await FeedApiQueries.Post(client, api).MutateAsync(new FeedApiQueries.PostVariables("news", "hi"));
            Assert.Equal(6, api.Calls.Count); // the active feed refetched its three pages

            var pageFn = FeedApiQueries.GetFeedPageFn(api, "sports");
            var sports = await pageFn(10, CancellationToken.None);
            Assert.Equal(new[] { 10, 11 }, sports.Items);
        }

        [Fact]
        public async Task Generated_generic_queries_are_keyed_by_type()
        {
            using var client = new QueryClient(TestUtil.NoRetry());
            var api = new FakeFeedApi();

            var widget = await client.FetchAsync(FeedApiQueries.GetItem<Widget>(api, 1));
            var gadget = await client.FetchAsync(FeedApiQueries.GetItem<Gadget>(api, 1));

            Assert.Equal(new Widget(1), widget.Data);
            Assert.Equal(new Gadget(1), gadget.Data);
            Assert.NotEqual(FeedApiQueries.Keys.GetItem<Widget>(1), FeedApiQueries.Keys.GetItem<Gadget>(1));
            Assert.True(FeedApiQueries.Keys.GetItemPrefix.IsPrefixOf(FeedApiQueries.Keys.GetItem<Widget>(1)));
        }

        [Fact]
        public async Task A_class_target_generates_queries_for_its_methods()
        {
            using var client = new QueryClient(TestUtil.NoRetry());
            var wrapper = new KiotaUsersWrapper();

            var fresh = new QueryOptions { StaleTime = TimeSpan.FromMinutes(1) };
            var user = await client.FetchAsync(KiotaUsersWrapperQueries.GetUser(wrapper, 3, fresh));
            await client.FetchAsync(KiotaUsersWrapperQueries.GetUser(wrapper, 3, fresh));

            Assert.Equal("user 3", user.Data);
            Assert.Equal(1, wrapper.Calls);
            Assert.Equal(QueryKey.Create("kiota", "GetUser", 3), KiotaUsersWrapperQueries.Keys.GetUser(3));
        }
    }
}
