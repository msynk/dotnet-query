using DotNetQuery;
using DotNetQuery.Sample.Chat;

// A console showcase of two DotNetQuery features working together for a chat UI:
//   1. an infinite query paginating message *history* (load older pages), and
//   2. StreamIntoCacheAsync folding a *live* message stream into the cache in real time.
// Deterministic and self-terminating so it doubles as an end-to-end smoke test (exit 0 == pass).

using var client = new QueryClient(new QueryClientOptions { DefaultStaleTime = TimeSpan.FromMinutes(5) });
var chat = new ChatService();

// --- 1. Infinite history --------------------------------------------------------------------------
Console.WriteLine("=== Chat history (infinite query) ===");
using var history = client.CreateInfiniteQuery<int, MessagePage>(
    QueryKey.Create("chat", "history"),
    (offset, ct) => chat.GetHistoryPageAsync(offset, ct),
    new InfiniteQueryOptions<int, MessagePage>
    {
        InitialPageParam = 0,
        GetNextPageParam = (last, _, _) => last.HasMore ? NextPageParam.Of(last.Offset + ChatService.PageSize) : NextPageParam.None<int>(),
    });

var afterFirst = await history.FetchAsync();
Console.WriteLine($"Loaded newest page: {afterFirst.Data.Pages[0].Messages.Count} messages " +
                  $"(newest is #{afterFirst.Data.Pages[0].Messages[0].Id}).");

var afterSecond = await history.FetchNextPageAsync();
var oldestPage = afterSecond.Data.Pages[^1];
Console.WriteLine($"Loaded older page: {oldestPage.Messages.Count} messages " +
                  $"(oldest so far is #{oldestPage.Messages[^1].Id}).");

var historyCount = afterSecond.Data.Pages.Sum(p => p.Messages.Count);
Console.WriteLine($"Total history loaded across {afterSecond.Data.Pages.Count} pages: {historyCount} messages.\n");

// --- 2. Live messages via StreamIntoCacheAsync ----------------------------------------------------
Console.WriteLine("=== Live messages (StreamIntoCacheAsync) ===");
var liveKey = QueryKey.Create("chat", "live");

// An observer prints each new message as the stream folds it into the cached list - exactly what a
// bound <Query> component or MVVM view would react to.
using var observer = client.Watch<List<Message>>(liveKey);
observer.StateChanged += state =>
{
    if (state is { HasData: true, Data.Count: > 0 })
    {
        var last = state.Data[^1];
        Console.WriteLine($"[live] {last.User}: {last.Text}   ({state.Data.Count} in feed)");
    }
};

await client.StreamIntoCacheAsync<List<Message>, Message>(
    liveKey,
    chat.LiveMessagesAsync(5),
    (feed, incoming) => [.. feed ?? [], incoming]);

var liveCount = client.GetQueryData<List<Message>>(liveKey).Data?.Count ?? 0;
Console.WriteLine($"\nLive feed received {liveCount} messages.");

var ok = historyCount == 20 && liveCount == 5;
Console.WriteLine(ok ? "\nDONE (pass)" : "\nDONE (FAIL)");
return ok ? 0 : 1;
